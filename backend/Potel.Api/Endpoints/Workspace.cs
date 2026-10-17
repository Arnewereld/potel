using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Data;
using Potel.Api.Payments;

namespace Potel.Api.Endpoints;

public record RegisterRequest(string Company, string Name, string Email, string Password, bool DemoData, string? Kvk, bool AcceptTerms, bool BusinessUse);
public record DeleteWorkspaceRequest(string Password);

// Begrenst het aanmaken van werkruimtes: per netwerk (IPv4 /24, IPv6 /64) en voor het hele platform samen, per uur.
// Een werkruimte is gratis, dus anders maakt één bron er zoveel als hij wil en vermenigvuldigt hij elke limiet per werkruimte.
public sealed class SignupThrottle(IConfiguration config) : IDisposable
{
    readonly PartitionedRateLimiter<string> perNetwork = PartitionedRateLimiter.Create<string, string>(network =>
        RateLimitPartition.GetFixedWindowLimiter(network, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimit:SignupsPerHour", 5), Window = TimeSpan.FromHours(1),
        }));

    readonly RateLimiter total = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
    {
        PermitLimit = config.GetValue("RateLimit:SignupsPerHourTotal", 100), Window = TimeSpan.FromHours(1),
    });

    // Het netwerk waar een adres in zit: de eerste drie getallen van een IPv4-adres, de eerste helft van een IPv6-adres.
    public static string Network(IPAddress? ip)
    {
        if (ip is null) return "onbekend";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? $"{bytes[0]}.{bytes[1]}.{bytes[2]}.0/24"
            : $"{Convert.ToHexString(bytes, 0, 8)}/64";
    }

    // Null als het mag, anders de melding.
    public string? TryStart(IPAddress? ip)
    {
        using (var lease = perNetwork.AttemptAcquire(Network(ip)))
            if (!lease.IsAcquired) return "Vanaf jouw netwerk zijn het afgelopen uur al een paar werkruimtes aangemaakt. Probeer het over een uur opnieuw.";
        using (var lease = total.AttemptAcquire())
            if (!lease.IsAcquired) return "Er melden zich nu erg veel mensen tegelijk aan. Probeer het over een uur opnieuw.";
        return null;
    }

    public void Dispose()
    {
        perNetwork.Dispose();
        total.Dispose();
    }
}

public static partial class WorkspaceEndpoints
{
    public const int TrialDays = 30;

    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9]{8}$")]
    private static partial System.Text.RegularExpressions.Regex KvkNumber();

    // Een KvK-nummer is 8 cijfers; spaties en punten mogen ertussen staan. Null als het geen geldig nummer is.
    public static string? NormalizeKvk(string? kvk)
    {
        var digits = (kvk ?? "").Replace(" ", "").Replace(".", "").Trim();
        return KvkNumber().IsMatch(digits) ? digits : null;
    }

    static object Dto(Workspace w, bool platformAdmin = false, bool onlinePayment = false) => new
    {
        w.Id, w.Name, w.Plan, w.TrialEndsAt, w.CreatedAt, platformAdmin, maxUsers = Plans.MaxUsers(w.Plan),
        onboarded = w.OnboardedAt != null, demoData = w.DemoDataAt != null,
        trialDaysLeft = w.Plan == Plans.Trial && w.TrialEndsAt is { } end ? Math.Max(0, (int)Math.Ceiling((end - DateTime.UtcNow).TotalDays)) : (int?)null,
        // Online betalen via Mollie staat aan, en hoe het abonnement ervoor staat.
        onlinePayment, subscriptionActive = w.MollieSubscriptionId != null, w.PaidUntil, w.SubscriptionCanceledAt,
        readOnly = w.ReadOnlyReason(DateTime.UtcNow),
        w.RequireTwoFactorForAdmins,
    };

    public static void MapWorkspace(this RouteGroupBuilder api)
    {
        // Zelf een account aanmaken: een nieuwe werkruimte met een proefperiode en jij als beheerder.
        api.MapPost("/auth/register", async (AppDb db, HttpContext http, SignupThrottle throttle, AccountMail mail, RegisterRequest req) =>
        {
            var email = (req.Email ?? "").Trim().ToLower();
            if (string.IsNullOrWhiteSpace(req.Company) || string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "Vul je naam en bedrijfsnaam in" });
            if (!email.Contains('@') || email.Length < 5) return Results.BadRequest(new { error = "Vul een geldig e-mailadres in" });
            if ((req.Password ?? "").Length < AuthEndpoints.MinPasswordLength)
                return Results.BadRequest(new { error = $"Kies een wachtwoord van minstens {AuthEndpoints.MinPasswordLength} tekens" });
            // Alleen voor bedrijven: met een KvK-nummer, voor zakelijk gebruik en met akkoord op de voorwaarden.
            if (NormalizeKvk(req.Kvk) is not { } kvk) return Results.BadRequest(new { error = "Vul je KvK-nummer in. Dat zijn 8 cijfers." });
            if (!req.BusinessUse) return Results.BadRequest(new { error = "Potel is alleen voor zakelijk gebruik. Bevestig dat je het voor je bedrijf gebruikt." });
            if (!req.AcceptTerms) return Results.BadRequest(new { error = "Ga akkoord met de algemene voorwaarden en de verwerkersovereenkomst om een account aan te maken." });
            if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == email))
                return Results.Conflict(new { error = AuthEndpoints.EmailUnavailable });
            if (throttle.TryStart(http.Connection.RemoteIpAddress) is { } tooMany)
                return Results.Json(new { error = tooMany }, statusCode: StatusCodes.Status429TooManyRequests);

            var ws = new Workspace { Name = req.Company.Trim(), TrialEndsAt = DateTime.UtcNow.AddDays(TrialDays), Kvk = kvk };
            db.Workspaces.Add(ws);
            await db.SaveChangesAsync();
            db.Tenant.WorkspaceId = ws.Id;

            db.Settings.Add(new Settings { CompanyName = ws.Name, OwnerName = req.Name.Trim(), Email = email, Kvk = kvk });
            var user = new User { Name = req.Name.Trim(), Email = email, Role = Roles.Admin, LastLoginAt = DateTime.UtcNow };
            user.PasswordHash = AuthEndpoints.Hash(user, req.Password!);
            db.Users.Add(user);
            db.Log("systeem", $"Werkruimte {ws.Name} aangemaakt");
            await db.SaveChangesAsync();
            // Vastleggen welke voorwaarden deze klant accepteerde, wanneer en wie dat deed.
            ws.TermsVersion = Terms.Version;
            ws.TermsAcceptedAt = DateTime.UtcNow;
            ws.TermsAcceptedByUserId = user.Id;
            ws.TermsAcceptedByEmail = user.Email;
            db.Log("systeem", $"Algemene voorwaarden en verwerkersovereenkomst (versie {Terms.Version}) geaccepteerd door {user.Email}");
            await db.SaveChangesAsync();
            if (req.DemoData) Seed.Run(db, sampleCompany: false);
            // Werken kan meteen; mail uit werkstromen pas na het bevestigen van het adres.
            await mail.SendVerificationAsync(user, http.Request);

            await AuthEndpoints.SignIn(http, user);
            return Results.Created("/api/workspace", UserDto.From(user));
        }).AllowAnonymous().RequireRateLimiting("auth");

        var g = api.MapGroup("/workspace");

        g.MapGet("/", async (AppDb db, ClaimsPrincipal user, MollieBilling billing) =>
            await db.Workspaces.FindAsync(db.TenantId) is { } w ? Results.Ok(Dto(w, await PlatformEndpoints.IsPlatformAdmin(db, user), billing.Enabled)) : Results.NotFound());

        g.MapPost("/onboarded", async (AppDb db) =>
        {
            var w = await db.Workspaces.FindAsync(db.TenantId);
            if (w is null) return Results.NotFound();
            w.OnboardedAt ??= DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(Dto(w));
        });

        // Inloggen in twee stappen verplicht maken voor alle beheerders van deze werkruimte. Wie het nog niet heeft, stelt het
        // bij de volgende stap in de app eerst in. Aanzetten kan alleen als je het zelf al gebruikt, anders sluit je jezelf even buiten.
        g.MapPut("/two-factor", async (AppDb db, ClaimsPrincipal me, MollieBilling billing, TwoFactorPolicyRequest req) =>
        {
            var w = await db.Workspaces.FindAsync(db.TenantId);
            var u = me.UserId() is { } id ? await db.Users.FindAsync(id) : null;
            if (w is null || u is null) return Results.NotFound();
            if (req.RequireForAdmins && !u.TwoFactorEnabled)
                return Results.BadRequest(new { error = "Zet eerst voor jezelf inloggen in twee stappen aan, bij Instellingen, Account. Daarna kun je het verplicht maken voor alle beheerders." });
            if (w.RequireTwoFactorForAdmins != req.RequireForAdmins)
            {
                w.RequireTwoFactorForAdmins = req.RequireForAdmins;
                db.Log("gebruiker", req.RequireForAdmins
                    ? $"{u.Name} heeft inloggen in twee stappen verplicht gemaakt voor beheerders"
                    : $"{u.Name} heeft inloggen in twee stappen niet meer verplicht voor beheerders");
                await db.SaveChangesAsync();
            }
            return Results.Ok(Dto(w, await PlatformEndpoints.IsPlatformAdmin(db, me), billing.Enabled));
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        g.MapPost("/demo-data", async (AppDb db) =>
        {
            if (await db.Customers.AnyAsync() || await db.Projects.AnyAsync())
                return Results.Conflict(new { error = "Er staan al gegevens in je werkruimte; voorbeelddata kan alleen in een lege werkruimte." });
            Seed.Run(db, sampleCompany: false);
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Haalt alle voorbeelddata weg, ook verstuurde voorbeeldfacturen. Wat je zelf hebt toegevoegd blijft staan, ook als het
        // aan een voorbeeldklant of -project hangt; die klant of dat project blijft dan ook.
        g.MapDelete("/demo-data", async (AppDb db) =>
        {
            await using var tx = await WriteLock.BeginAsync(db);
            // Voorbeelduren die je zelf op een echte factuur zette, blijven staan; die factuur moet blijven kloppen.
            db.TimeEntries.RemoveRange(db.TimeEntries.Where(t => t.IsDemo && (t.InvoiceId == null || db.Invoices.Any(i => i.Id == t.InvoiceId && i.IsDemo))));
            // Voor ritten hetzelfde.
            db.Trips.RemoveRange(db.Trips.Where(t => t.IsDemo && (t.InvoiceId == null || db.Invoices.Any(i => i.Id == t.InvoiceId && i.IsDemo))));
            db.TripRoutes.RemoveRange(db.TripRoutes.Where(r => r.IsDemo));
            // Voorbeeldoffertes gaan weg, met hun regels en links. Een voorbeeldlead met een eigen offerte van jou blijft staan.
            db.Quotes.RemoveRange(db.Quotes.Where(q => q.IsDemo));
            db.Invoices.RemoveRange(db.Invoices.Where(i => i.IsDemo && !i.Credits.Any()));
            db.RecurringInvoices.RemoveRange(db.RecurringInvoices.Where(r => r.IsDemo));
            db.Leads.RemoveRange(db.Leads.Where(l => l.IsDemo && !db.Quotes.Any(q => q.LeadId == l.Id && !q.IsDemo)));
            db.Appointments.RemoveRange(db.Appointments.Where(a => a.IsDemo));
            db.CustomRecords.RemoveRange(db.CustomRecords.Where(r => r.IsDemo));
            var workflows = await db.Workflows.Where(w => w.IsDemo).Select(w => w.Id).ToListAsync();
            db.WorkflowRuns.RemoveRange(db.WorkflowRuns.Where(r => workflows.Contains(r.WorkflowId)));
            db.Workflows.RemoveRange(db.Workflows.Where(w => w.IsDemo));
            // Voorbeeldvaste kosten gaan weg; een uitgave die blijft, hangt daarna nergens meer aan.
            var demoSubscriptions = await db.RecurringExpenses.Where(r => r.IsDemo).Select(r => r.Id).ToListAsync();
            await db.Expenses.Where(e => e.RecurringExpenseId != null && demoSubscriptions.Contains(e.RecurringExpenseId.Value))
                .ExecuteUpdateAsync(x => x.SetProperty(e => e.RecurringExpenseId, (int?)null).SetProperty(e => e.RecurringPeriodStart, (DateTime?)null));
            db.RecurringExpenses.RemoveRange(db.RecurringExpenses.Where(r => r.IsDemo));
            // Voorbeeldkosten gaan weg, behalve als je er zelf een bonnetje bij zette: die uitgave is dan van jou en telt voortaan mee.
            await db.Expenses.Where(e => e.IsDemo && db.ExpenseReceipts.Any(r => r.ExpenseId == e.Id)).ExecuteUpdateAsync(x => x.SetProperty(e => e.IsDemo, false));
            db.Expenses.RemoveRange(db.Expenses.Where(e => e.IsDemo));
            await db.SaveChangesAsync();
            // Een voorbeeldproject met een lopende timer blijft ook staan: die tijd is van jou.
            db.Projects.RemoveRange(db.Projects.Where(p => p.IsDemo && !db.TimeEntries.Any(t => t.ProjectId == p.Id) && !db.Expenses.Any(e => e.ProjectId == p.Id)
                && !db.Trips.Any(t => t.ProjectId == p.Id) && !db.TripRoutes.Any(r => r.ProjectId == p.Id) && !db.RunningTimers.Any(t => t.ProjectId == p.Id)
                && !db.RecurringExpenses.Any(r => r.ProjectId == p.Id)));
            db.CustomModules.RemoveRange(db.CustomModules.Where(m => m.IsDemo && !db.CustomRecords.Any(r => r.ModuleId == m.Id)));
            await db.SaveChangesAsync();
            var customers = await db.Customers
                .Where(c => c.IsDemo && !db.Invoices.Any(i => i.CustomerId == c.Id) && !db.Projects.Any(p => p.CustomerId == c.Id) && !db.Expenses.Any(e => e.CustomerId == c.Id)
                            && !db.RecurringInvoices.Any(r => r.CustomerId == c.Id) && !db.Quotes.Any(q => q.CustomerId == c.Id && !q.IsDemo)
                            && !db.Trips.Any(t => t.CustomerId == c.Id) && !db.TripRoutes.Any(r => r.CustomerId == c.Id)
                            && !db.RecurringExpenses.Any(r => r.CustomerId == c.Id))
                .Select(c => c.Id).ToListAsync();
            await db.Appointments.Where(a => a.CustomerId != null && customers.Contains(a.CustomerId.Value)).ExecuteUpdateAsync(x => x.SetProperty(a => a.CustomerId, (int?)null));
            await db.Leads.Where(l => l.CustomerId != null && customers.Contains(l.CustomerId.Value)).ExecuteUpdateAsync(x => x.SetProperty(l => l.CustomerId, (int?)null));
            db.Customers.RemoveRange(db.Customers.Where(c => customers.Contains(c.Id)));
            if (await db.Workspaces.FindAsync(db.TenantId) is { } w) w.DemoDataAt = null;
            db.Log("systeem", "Voorbeelddata verwijderd");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Al je gegevens in één JSON-bestand, bijvoorbeeld voor je administratie of om over te stappen, met de bonnetjes als base64.
        // Het bestand wordt in stukjes geschreven en de bonnetjes één voor één gelezen, zodat er nooit alles tegelijk in het geheugen staat.
        g.MapGet("/export", async (AppDb db, HttpContext http, IOptions<JsonOptions> json) =>
        {
            var options = json.Value.SerializerOptions;
            http.Response.ContentType = "application/json; charset=utf-8";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"potel-export-{DateTime.UtcNow:yyyy-MM-dd}.json\"";
            // Via de PipeWriter, want de serializer schrijft anders synchroon naar de response.
            var body = http.Response.BodyWriter;
            await using var w = new Utf8JsonWriter(body, new JsonWriterOptions { Indented = true, Encoder = options.Encoder });
            async Task Flush()
            {
                w.Flush();
                await body.FlushAsync();
            }
            async Task Write<T>(string name, T value)
            {
                w.WritePropertyName(name);
                JsonSerializer.Serialize(w, value, options);
                await Flush();
            }

            w.WriteStartObject();
            await Write("exportedAt", DateTime.UtcNow);
            await Write("workspace", await db.Workspaces.FindAsync(db.TenantId));
            await Write("settings", await db.Settings.FirstOrDefaultAsync());
            await Write("customers", await db.Customers.ToListAsync());
            await Write("projects", await db.Projects.ToListAsync());
            await Write("timeEntries", await db.TimeEntries.ToListAsync());
            await Write("invoices", await db.Invoices.Include(i => i.Lines).Include(i => i.Payments).ToListAsync());
            // De urenspecificaties zoals ze bij versturen zijn vastgelegd.
            await Write("invoiceHourSpecifications", (await db.Invoices.Where(i => i.HoursSnapshot != null).OrderBy(i => i.Id)
                .Select(i => new { i.Id, i.Number, i.HoursSnapshot }).ToListAsync())
                .Select(i => new { invoiceId = i.Id, number = i.Number, specification = Invoicing.HourSpecs.Parse(i.HoursSnapshot) }));
            // Offertes met hun regels en het akkoord van de klant (naam, tijd en IP-adres). De links zelf gaan niet mee.
            await Write("quotes", await db.Quotes.Include(q => q.Lines.OrderBy(l => l.Position)).OrderBy(q => q.Id).ToListAsync());
            await Write("recurringInvoices", await db.RecurringInvoices.Include(r => r.Lines).ToListAsync());
            await Write("invoiceMails", await db.InvoiceMails.OrderBy(m => m.Id).ToListAsync());
            await Write("reminderSettings", await db.ReminderSettings.FirstOrDefaultAsync());
            await Write("customerPortals", await db.CustomerPortals.OrderBy(p => p.Id).ToListAsync());
            // Het GitHub-token zelf gaat nooit mee, alleen de laatste 4 tekens.
            await Write("github", await db.GitHubSettings.FirstOrDefaultAsync());
            // Van de eigen Mollie alleen of er een sleutel is (test of live, laatste 4 tekens), nooit de sleutel zelf.
            await Write("mollie", await db.MollieAccounts.FirstOrDefaultAsync());
            await Write("onlinePayments", await db.OnlinePayments.OrderBy(p => p.Id).ToListAsync());
            await Write("githubAuthors", await db.GitHubAuthors.OrderBy(a => a.Id).ToListAsync());
            await Write("githubCommits", await db.GitHubCommits.OrderBy(c => c.Id).ToListAsync());
            await Write("expenses", await db.Expenses.OrderBy(e => e.Id).ToListAsync());
            // Vaste kosten: abonnementen en andere kosten die elke maand, elk kwartaal of elk jaar terugkomen.
            await Write("recurringExpenses", await db.RecurringExpenses.OrderBy(r => r.Id).ToListAsync());
            // De kilometerregistratie en de vaste routes.
            await Write("trips", await db.Trips.OrderBy(t => t.Id).ToListAsync());
            await Write("tripRoutes", await db.TripRoutes.OrderBy(r => r.Id).ToListAsync());
            // Ingelezen bankafschriften: de regels, de imports en de kolomindeling voor CSV.
            await Write("bankImports", await db.BankImports.OrderBy(i => i.Id).ToListAsync());
            await Write("bankTransactions", await db.BankTransactions.OrderBy(t => t.Id).ToListAsync());
            await Write("bankCsvMapping", await db.BankCsvMappings.FirstOrDefaultAsync());
            // De belastingpot per jaar (loon, voorlopige aanslag, hoe je reserveert) en welke btw-aangiftes je als gedaan markeerde.
            await Write("taxReserveYears", await db.TaxReserveYears.OrderBy(r => r.Year).ToListAsync());
            await Write("vatFilings", await db.VatFilings.OrderBy(f => f.Year).ThenBy(f => f.Quarter).ToListAsync());
            // De instellingen van de cashflow-prognose: je banksaldo, vaste kosten en hoe offertes binnenkomen.
            await Write("cashflowSettings", await db.CashflowSettings.FirstOrDefaultAsync());
            await Write("leads", await db.Leads.ToListAsync());
            await Write("appointments", await db.Appointments.ToListAsync());
            // Wat iedereen in zijn agendalink wil zien en wanneer die werd opgehaald. De links zelf gaan niet mee.
            await Write("calendarFeeds", await db.CalendarFeeds.OrderBy(f => f.Id).ToListAsync());
            // Welke meldingen iedereen wil krijgen, in de app en per e-mail. De meldingen zelf zijn tijdelijk en gaan niet mee.
            await Write("notificationSettings", await db.NotificationSettings.OrderBy(s => s.Id).ToListAsync());
            await Write("modules", await db.CustomModules.ToListAsync());
            await Write("records", await db.CustomRecords.ToListAsync());
            await Write("workflows", await db.Workflows.ToListAsync());
            await Write("users", (await db.Users.ToListAsync()).Select(UserDto.From));
            w.WritePropertyName("receipts");
            w.WriteStartArray();
            foreach (var id in await db.ExpenseReceipts.OrderBy(r => r.Id).Select(r => r.Id).ToListAsync())
            {
                var r = await db.ExpenseReceipts.AsNoTracking().FirstAsync(x => x.Id == id);
                w.WriteStartObject();
                w.WriteNumber("expenseId", r.ExpenseId);
                w.WriteString("fileName", r.FileName);
                w.WriteString("contentType", r.ContentType);
                w.WriteNumber("size", r.Size);
                w.WriteString("uploadedAt", r.UploadedAt);
                w.WriteBase64String("data", r.Data);
                w.WriteEndObject();
                await Flush();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            await Flush();
            return Results.Empty;
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // Verwijdert de hele werkruimte met alle gegevens. Alleen een beheerder, met wachtwoord.
        g.MapDelete("/", async (AppDb db, HttpContext http, ClaimsPrincipal me, MollieBilling billing, [Microsoft.AspNetCore.Mvc.FromBody] DeleteWorkspaceRequest req) =>
        {
            var user = me.UserId() is { } id ? await db.Users.FindAsync(id) : null;
            if (user is null) return Results.Unauthorized();
            if (new PasswordHasher<User>().VerifyHashedPassword(user, user.PasswordHash, req.Password ?? "") == PasswordVerificationResult.Failed)
                return Results.BadRequest(new { error = "Je wachtwoord klopt niet" });
            // Een platformbeheerder in deze werkruimte verdwijnt mee. Dat mag alleen een platformbeheerder zelf beslissen,
            // en de laatste platformbeheerder kan zichzelf zo niet weghalen.
            if (await db.Users.AnyAsync(u => u.IsPlatformAdmin && u.Id != user.Id) && !user.IsPlatformAdmin)
                return Results.Json(new { error = "In deze werkruimte zit een account dat ook het platform beheert. Vraag een platformbeheerder eerst die rechten weg te halen." }, statusCode: StatusCodes.Status403Forbidden);
            if (user.IsPlatformAdmin && !await db.Users.IgnoreQueryFilters().AnyAsync(u => u.IsPlatformAdmin && u.WorkspaceId != user.WorkspaceId))
                return Results.Conflict(new { error = "Je bent de laatste platformbeheerder. Wijs eerst op de pagina Platform iemand uit een andere werkruimte aan, anders kan niemand het platform meer beheren." });

            // Eerst het abonnement bij Mollie stoppen, zodat er na het verwijderen niets meer wordt afgeschreven.
            if (await db.Workspaces.FindAsync(db.TenantId) is { } current && !await billing.StopBeforeDeleteAsync(current))
                return Results.Json(new { error = "Je abonnement kon niet worden opgezegd, dus je werkruimte is nog niet verwijderd. Probeer het over een paar minuten opnieuw." }, statusCode: StatusCodes.Status502BadGateway);

            // Volgorde maakt uit vanwege de koppelingen tussen tabellen. Bonnetjes direct in de database, zonder ze eerst in te lezen.
            await db.ExpenseReceipts.ExecuteDeleteAsync();
            await db.RunningTimers.ExecuteDeleteAsync();
            await db.Trips.ExecuteDeleteAsync();
            await db.TripRoutes.ExecuteDeleteAsync();
            await db.TaxReserveYears.ExecuteDeleteAsync();
            await db.VatFilings.ExecuteDeleteAsync();
            await db.CashflowSettings.ExecuteDeleteAsync();
            await db.BankTransactions.ExecuteDeleteAsync();
            await db.BankImports.ExecuteDeleteAsync();
            await db.BankCsvMappings.ExecuteDeleteAsync();
            await db.Expenses.ExecuteDeleteAsync();
            await db.RecurringExpenses.ExecuteDeleteAsync();
            await db.InvoiceMails.ExecuteDeleteAsync();
            await db.InvoicePayments.ExecuteDeleteAsync();
            await db.OnlinePayments.ExecuteDeleteAsync();
            await db.MollieAccounts.ExecuteDeleteAsync();
            await db.InvoiceLinks.ExecuteDeleteAsync();
            // Offerteregels gaan mee met hun offerte (cascade in de database).
            await db.QuoteLinks.ExecuteDeleteAsync();
            await db.Quotes.ExecuteDeleteAsync();
            await db.PortalLinks.ExecuteDeleteAsync();
            await db.CustomerPortals.ExecuteDeleteAsync();
            await db.GitHubCommits.ExecuteDeleteAsync();
            await db.GitHubAuthors.ExecuteDeleteAsync();
            await db.GitHubSettings.ExecuteDeleteAsync();
            await db.CalendarFeeds.ExecuteDeleteAsync();
            await db.Notifications.ExecuteDeleteAsync();
            await db.NotificationSettings.ExecuteDeleteAsync();
            db.TimeEntries.RemoveRange(db.TimeEntries);
            db.Invoices.RemoveRange(db.Invoices);
            await db.SaveChangesAsync();
            db.RecurringInvoices.RemoveRange(db.RecurringInvoices);
            db.ReminderSettings.RemoveRange(db.ReminderSettings);
            db.Projects.RemoveRange(db.Projects);
            db.Appointments.RemoveRange(db.Appointments);
            db.Leads.RemoveRange(db.Leads);
            await db.SaveChangesAsync();
            db.Customers.RemoveRange(db.Customers);
            db.CustomRecords.RemoveRange(db.CustomRecords);
            db.CustomModules.RemoveRange(db.CustomModules);
            db.WorkflowRuns.RemoveRange(db.WorkflowRuns);
            db.Workflows.RemoveRange(db.Workflows);
            db.Activities.RemoveRange(db.Activities);
            db.Settings.RemoveRange(db.Settings);
            db.AccountTokens.RemoveRange(db.AccountTokens);
            db.RecoveryCodes.RemoveRange(db.RecoveryCodes);
            db.MolliePayments.RemoveRange(db.MolliePayments);
            db.Users.RemoveRange(db.Users);
            if (await db.Workspaces.FindAsync(db.TenantId) is { } w) db.Workspaces.Remove(w);
            await db.SaveChangesAsync();
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).RequireAuthorization(p => p.RequireRole(Roles.Admin));
    }
}

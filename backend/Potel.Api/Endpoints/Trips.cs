using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Endpoints;

// Wat de frontend meestuurt. Met een kilometerstand voor en na de rit rekenen we de kilometers zelf uit.
public record TripInput(
    DateTime Date, string? From, string? To, decimal? Kilometres, string? Purpose, string? Kind, int? CustomerId, int? ProjectId,
    int? OdometerStart, int? OdometerEnd, bool Billable);

public record TripRouteInput(string? Name, string? From, string? To, decimal Kilometres, string? Purpose, string? Kind, int? CustomerId, int? ProjectId, bool Billable);

public record AddTripsRequest(int[]? TripIds);

public record TripDto(
    int Id, DateTime Date, string From, string To, decimal Kilometres, string? Purpose, string Kind, string KindLabel,
    int? CustomerId, string? CustomerName, int? ProjectId, string? ProjectName, string? ProjectColor, int? OdometerStart, int? OdometerEnd,
    bool Billable, decimal? Rate, int? InvoiceId, string? InvoiceNumber, int? UserId, string? UserName, bool Demo, bool CanEdit);

public record TripRouteDto(
    int Id, string Name, string From, string To, decimal Kilometres, string? Purpose, string Kind, int? CustomerId, int? ProjectId, bool Billable);

public record TripMonth(string Month, int Trips, decimal BusinessKm, decimal CompanyCarKm, decimal PrivateKm);

public record TripYearDto(
    int Year, List<TripDto> Trips, List<TripMonth> Months, int Count, decimal BusinessKm, decimal CompanyCarKm, decimal PrivateKm,
    decimal? Rate, decimal Deduction, string RateSource, string? Note, string KilometreSource);

// Open ritten voor een factuur, per kilometertarief. Ritten zonder tarief tellen apart: die kunnen nog niet op een factuur.
public record BillableLine(decimal Rate, decimal Kilometres, decimal Amount, int Trips);
public record BillableTripsDto(int Trips, decimal Kilometres, decimal Amount, List<BillableLine> Lines, int WithoutRate, decimal WithoutRateKilometres);

// Kilometerregistratie. Iedereen in de werkruimte mag ritten vastleggen en ziet alle ritten; je eigen ritten pas je aan of
// verwijder je zelf, een beheerder kan alle ritten aanpassen. Een rit op een factuur ligt vast, net als uren.
public static class TripEndpoints
{
    public const decimal MaxKilometres = 10_000;
    public const int MaxOdometer = 10_000_000;
    public const decimal MaxRate = 10;
    public const string TripsTaken = "Een deel van deze ritten is net op een andere factuur gezet. Vernieuw de pagina en probeer het opnieuw.";
    public const string OwnTripsOnly = "Je kunt alleen je eigen ritten aanpassen of verwijderen. Vraag een beheerder als er iets niet klopt.";

    static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    public static decimal RoundKm(decimal km) => Math.Round(km, 1, MidpointRounding.AwayFromZero);

    // 90 of 90,5, zoals het op een factuur staat.
    public static string Km(decimal km) => km.ToString("#,0.#", Dutch);


    // De omschrijving van een factuurregel met ritten, in de taal van de factuur.
    public static string LineText(decimal km, decimal rate, string? language = null) =>
        Invoicing.ClientTexts.T(language, "line.trips", ("km", Invoicing.ClientTexts.Number(language, km, "#,0.#")), ("rate", Invoicing.ClientTexts.Euro(language, rate)));

    // Een kilometertarief: leeg, of tussen 0 en 10 euro op centen.
    public static string? RateProblem(decimal? rate) => rate is < 0 or > MaxRate
        ? "Vul een kilometertarief in tussen € 0 en € 10, of laat het leeg."
        : null;

    public static decimal? CleanRate(decimal? rate) => rate is { } r ? Money.Round(r) : null;

    static bool CanEdit(ClaimsPrincipal user, Trip t) => user.IsInRole(Roles.Admin) || (t.UserId is { } id && id == user.UserId());

    static string Locked(AppDb db, Trip t) =>
        db.Invoices.Where(i => i.Id == t.InvoiceId).Select(i => i.Number).FirstOrDefault() is { } number
            ? $"Deze rit staat op factuur {number} en ligt vast."
            : "Deze rit staat op een conceptfactuur. Haal hem daar van de factuur af om hem weer vrij te geven.";

    static string? Text(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Controleert de rit en vult de afgeleide velden in: kilometers uit de kilometerstand en de klant van het project.
    // Klant en project moeten in de eigen werkruimte bestaan; het queryfilter houdt die van een ander buiten beeld.
    // Een vaste route heeft geen datum en hoeft geen doel te hebben.
    static async Task<string?> ApplyAsync(AppDb db, Trip t, TripInput input, bool route = false)
    {
        if (!route && input.Date.Year is < 2000 or > 2100) return "Vul een geldige datum in";
        var kind = input.Kind ?? TripKinds.Business;
        if (!TripKinds.All.Contains(kind)) return "Kies wat voor rit het is";
        var from = Text(input.From);
        var to = Text(input.To);
        if (from is null || to is null) return "Vul in waar je vandaan kwam en waar je naartoe ging";
        if (from.Length > 200 || to.Length > 200) return "Een adres mag maximaal 200 tekens zijn";
        var purpose = Text(input.Purpose);
        if (purpose is { Length: > 500 }) return "Het doel mag maximaal 500 tekens zijn";
        if (purpose is null && TripKinds.IsBusiness(kind) && !route) return "Vul het doel van de rit in, bijvoorbeeld het bezoek aan een klant";

        if (input.OdometerStart is < 0 or > MaxOdometer || input.OdometerEnd is < 0 or > MaxOdometer) return "Die kilometerstand klopt niet";
        var km = input.Kilometres ?? 0;
        if (input is { OdometerStart: { } start, OdometerEnd: { } end })
        {
            if (end <= start) return "De kilometerstand na de rit moet hoger zijn dan ervoor";
            km = end - start;
        }
        km = RoundKm(km);
        if (km <= 0) return "Vul het aantal kilometers in";
        if (km > MaxKilometres) return "Meer dan 10.000 kilometer voor één rit kan niet";

        int? customerId = null, projectId = null;
        var billable = false;
        if (kind != TripKinds.Private)
        {
            if (input.ProjectId is { } pid)
            {
                var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid);
                if (project is null) return "Kies een project";
                if (input.CustomerId is { } cid && cid != project.CustomerId) return "Dit project hoort bij een andere klant";
                projectId = project.Id;
                customerId = project.CustomerId;
            }
            else if (input.CustomerId is { } cid)
            {
                if (!await db.Customers.AnyAsync(c => c.Id == cid)) return "Kies een klant";
                customerId = cid;
            }
            billable = input.Billable;
            if (billable && customerId is null) return "Kies een klant of project om de rit door te belasten";
        }
        else if (input.Billable) return "Een privérit kun je niet doorbelasten";

        t.Date = input.Date.Date; t.From = from; t.To = to; t.Kilometres = km; t.Purpose = purpose; t.Kind = kind;
        t.CustomerId = customerId; t.ProjectId = projectId; t.Billable = billable;
        t.OdometerStart = input.OdometerStart; t.OdometerEnd = input.OdometerEnd;
        return null;
    }

    static async Task<string?> ApplyRouteAsync(AppDb db, TripRoute r, TripRouteInput input)
    {
        var trip = new Trip();
        if (await ApplyAsync(db, trip, new TripInput(default, input.From, input.To, input.Kilometres, input.Purpose, input.Kind,
                input.CustomerId, input.ProjectId, null, null, input.Billable), route: true) is { } error)
            return error;
        var generated = $"{trip.From} naar {trip.To}";
        var name = Text(input.Name) ?? (generated.Length > 100 ? generated[..100] : generated);
        if (name.Length > 100) return "De naam mag maximaal 100 tekens zijn";
        r.Name = name; r.From = trip.From; r.To = trip.To; r.Kilometres = trip.Kilometres; r.Purpose = trip.Purpose; r.Kind = trip.Kind;
        r.CustomerId = trip.CustomerId; r.ProjectId = trip.ProjectId; r.Billable = trip.Billable;
        return null;
    }

    // Het kilometertarief voor een rit: dat van het project, anders dat van de klant. De klant van een project is die van het project nu.
    static (int? CustomerId, decimal? Rate) RateFor(Trip t, IReadOnlyDictionary<int, Project> projects, IReadOnlyDictionary<int, Customer> customers)
    {
        var project = t.ProjectId is { } pid ? projects.GetValueOrDefault(pid) : null;
        var customerId = project?.CustomerId ?? t.CustomerId;
        var customer = customerId is { } cid ? customers.GetValueOrDefault(cid) : null;
        return (customerId, project?.KilometreRate ?? customer?.KilometreRate);
    }

    static async Task<List<TripDto>> DtosAsync(AppDb db, ClaimsPrincipal user, List<Trip> trips)
    {
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id);
        var customers = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id);
        var invoiceIds = trips.Where(t => t.InvoiceId != null).Select(t => t.InvoiceId!.Value).Distinct().ToList();
        var numbers = await db.Invoices.Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number);
        var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Name);
        return trips.Select(t =>
        {
            var (customerId, rate) = RateFor(t, projects, customers);
            var customer = customerId is { } cid ? customers.GetValueOrDefault(cid) : null;
            var project = t.ProjectId is { } pid ? projects.GetValueOrDefault(pid) : null;
            return new TripDto(t.Id, t.Date, t.From, t.To, t.Kilometres, t.Purpose, t.Kind, TripKinds.Label(t.Kind),
                customerId, customer is null ? null : string.IsNullOrWhiteSpace(customer.Company) ? customer.Name : customer.Company,
                t.ProjectId, project?.Name, project?.Color, t.OdometerStart, t.OdometerEnd, t.Billable, t.Billable ? rate : null,
                t.InvoiceId, t.InvoiceId is { } iid ? numbers.GetValueOrDefault(iid) : null,
                t.UserId, t.UserId is { } uid ? users.GetValueOrDefault(uid) : null, t.IsDemo, CanEdit(user, t));
        }).ToList();
    }

    // Open, door te belasten ritten met hun klant en tarief: van één klant, of van één project. Optioneel alleen de gekozen ritten.
    public static async Task<List<(Trip Trip, decimal? Rate)>> OpenTripsAsync(AppDb db, int? customerId, int? projectId = null, int[]? ids = null)
    {
        var q = db.Trips.AsNoTracking().Where(t => t.Billable && t.InvoiceId == null && t.Kind != TripKinds.Private);
        if (projectId is { } pid) q = q.Where(t => t.ProjectId == pid);
        if (ids is { Length: > 0 }) q = q.Where(t => ids.Contains(t.Id));
        var trips = await q.OrderBy(t => t.Date).ThenBy(t => t.Id).ToListAsync();
        var projects = await db.Projects.AsNoTracking().ToDictionaryAsync(p => p.Id);
        var customers = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id);
        return trips.Select(t => (Trip: t, Billing: RateFor(t, projects, customers)))
            .Where(x => customerId is null || x.Billing.CustomerId == customerId)
            .Select(x => (x.Trip, x.Billing.Rate)).ToList();
    }

    // Factuurregels voor ritten: één regel per kilometertarief, met de ritten die erbij horen.
    public static List<(InvoiceLine Line, List<Trip> Trips)> TripLines(IEnumerable<(Trip Trip, decimal? Rate)> trips, string? language = null) =>
        trips.Where(x => x.Rate is not null).GroupBy(x => x.Rate!.Value).OrderBy(g => g.Min(x => x.Trip.Date))
            .Select(g =>
            {
                var km = g.Sum(x => x.Trip.Kilometres);
                return (new InvoiceLine
                {
                    Description = LineText(km, g.Key, language), Quantity = km, Unit = "km", UnitPrice = g.Key,
                }, g.Select(x => x.Trip).ToList());
            }).ToList();

    // Koppelt de ritten aan hun factuurregel, maar alleen als ze nog vrij zijn en nog door te belasten. Lukt dat niet voor
    // allemaal (een ander verzoek was net eerder), dan false: draai dan alles terug. Hoort binnen een WriteLock, na het opslaan van de regels.
    public static async Task<bool> ClaimAsync(AppDb db, int invoiceId, List<(InvoiceLine Line, List<Trip> Trips)> parts)
    {
        foreach (var (line, trips) in parts.Where(x => x.Trips.Count > 0))
        {
            var ids = trips.Select(t => t.Id).ToList();
            var lineId = line.Id;
            var claimed = await db.Trips.Where(t => ids.Contains(t.Id) && t.InvoiceId == null && t.Billable)
                .ExecuteUpdateAsync(x => x.SetProperty(t => t.InvoiceId, invoiceId).SetProperty(t => t.InvoiceLineId, lineId));
            if (claimed != ids.Count) return false;
        }
        return true;
    }

    // Kilometers per soort, en de aftrek voor zakelijke kilometers met je eigen auto.
    public static (decimal Business, decimal CompanyCar, decimal Private) Totals(IEnumerable<Trip> trips)
    {
        var list = trips.ToList();
        return (list.Where(t => t.Kind == TripKinds.Business).Sum(t => t.Kilometres),
            list.Where(t => t.Kind == TripKinds.CompanyCar).Sum(t => t.Kilometres),
            list.Where(t => t.Kind == TripKinds.Private).Sum(t => t.Kilometres));
    }

    public static void MapTrips(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/trips");

        // De ritten van een jaar met de totalen per maand; zonder jaar het lopende jaar. Voorbeeldritten staan erbij, maar tellen
        // niet mee voor de aftrek, net als op het jaaroverzicht.
        g.MapGet("/", async (AppDb db, BusinessClock clock, ClaimsPrincipal user, int? year) =>
        {
            var y = year ?? clock.Today.Year;
            if (y is < 2000 or > 2100) return Results.BadRequest(new { error = "Kies een geldig jaar." });
            var from = new DateTime(y, 1, 1);
            var to = new DateTime(y, 12, 31);
            var trips = await db.Trips.AsNoTracking().Where(t => t.Date >= from && t.Date <= to)
                .OrderByDescending(t => t.Date).ThenByDescending(t => t.Id).ToListAsync();
            var months = Enumerable.Range(1, 12).Select(m =>
            {
                var (business, companyCar, priv) = Totals(trips.Where(t => t.Date.Month == m));
                return new TripMonth($"{y}-{m:00}", trips.Count(t => t.Date.Month == m), business, companyCar, priv);
            }).ToList();
            var (b, c, p) = Totals(trips);
            var rate = TaxYears.KilometreRateFor(y);
            var deduction = rate.Rate is { } r ? Money.Round(Totals(trips.Where(t => !t.IsDemo)).Business * r) : 0;
            return Results.Ok(new TripYearDto(y, await DtosAsync(db, user, trips), months, trips.Count, b, c, p, rate.Rate, deduction, rate.Source, rate.Note,
                TaxYears.KilometreSource));
        });

        g.MapGet("/{id:int}", async (AppDb db, ClaimsPrincipal user, int id) =>
            await db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id) is { } t ? Results.Ok((await DtosAsync(db, user, [t]))[0]) : Results.NotFound());

        g.MapPost("/", async (AppDb db, ClaimsPrincipal user, TripInput input) =>
        {
            var t = new Trip { UserId = user.UserId() };
            if (await ApplyAsync(db, t, input) is { } error) return Results.BadRequest(new { error });
            db.Trips.Add(t);
            await db.SaveChangesAsync();
            return Results.Created($"/api/trips/{t.Id}", (await DtosAsync(db, user, [t]))[0]);
        });

        g.MapPut("/{id:int}", async (AppDb db, ClaimsPrincipal user, int id, TripInput input) =>
        {
            var t = await db.Trips.FindAsync(id);
            if (t is null) return Results.NotFound();
            if (!CanEdit(user, t)) return Results.Json(new { error = OwnTripsOnly }, statusCode: StatusCodes.Status403Forbidden);
            if (t.InvoiceId is not null) return Results.Conflict(new { error = Locked(db, t) });
            if (await ApplyAsync(db, t, input) is { } error) return Results.BadRequest(new { error });
            await db.SaveChangesAsync();
            return Results.Ok((await DtosAsync(db, user, [t]))[0]);
        });

        g.MapDelete("/{id:int}", async (AppDb db, ClaimsPrincipal user, int id) =>
        {
            var t = await db.Trips.FindAsync(id);
            if (t is null) return Results.NotFound();
            if (!CanEdit(user, t)) return Results.Json(new { error = OwnTripsOnly }, statusCode: StatusCodes.Status403Forbidden);
            if (t.InvoiceId is not null) return Results.Conflict(new { error = Locked(db, t) });
            db.Trips.Remove(t);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // De terugweg: dezelfde rit de andere kant op, op dezelfde dag. Met een kilometerstand gaat die verder waar de heenweg eindigde.
        g.MapPost("/{id:int}/return", async (AppDb db, ClaimsPrincipal user, int id) =>
        {
            var source = await db.Trips.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
            if (source is null) return Results.NotFound();
            int? start = null, end = null;
            if (source is { OdometerStart: { } s, OdometerEnd: { } e } && e + (e - s) <= MaxOdometer) (start, end) = (e, e + (e - s));
            var t = new Trip { UserId = user.UserId() };
            var input = new TripInput(source.Date, source.To, source.From, source.Kilometres, source.Purpose, source.Kind,
                source.ProjectId is null ? source.CustomerId : null, source.ProjectId, start, end, source.Billable);
            if (await ApplyAsync(db, t, input) is { } error) return Results.BadRequest(new { error });
            db.Trips.Add(t);
            await db.SaveChangesAsync();
            return Results.Created($"/api/trips/{t.Id}", (await DtosAsync(db, user, [t]))[0]);
        });

        // Wat er van een klant of project nog door te belasten is, per kilometertarief.
        g.MapGet("/billable", async (AppDb db, int? customerId, int? projectId) =>
        {
            if (customerId is null && projectId is null) return Results.BadRequest(new { error = "Kies een klant of project" });
            var open = await OpenTripsAsync(db, customerId, projectId);
            var lines = TripLines(open).Select(x => new BillableLine(x.Line.UnitPrice, x.Line.Quantity, Money.LineAmount(x.Line), x.Trips.Count)).ToList();
            var noRate = open.Where(x => x.Rate is null).ToList();
            return Results.Ok(new BillableTripsDto(lines.Sum(l => l.Trips), lines.Sum(l => l.Kilometres), lines.Sum(l => l.Amount), lines,
                noRate.Count, noRate.Sum(x => x.Trip.Kilometres)));
        });

        var routes = g.MapGroup("/routes");

        routes.MapGet("/", async (AppDb db) =>
            (await db.TripRoutes.AsNoTracking().ToListAsync()).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(r => new TripRouteDto(r.Id, r.Name, r.From, r.To, r.Kilometres, r.Purpose, r.Kind, r.CustomerId, r.ProjectId, r.Billable)));

        routes.MapPost("/", async (AppDb db, TripRouteInput input) =>
        {
            var r = new TripRoute();
            if (await ApplyRouteAsync(db, r, input) is { } error) return Results.BadRequest(new { error });
            db.TripRoutes.Add(r);
            await db.SaveChangesAsync();
            return Results.Created($"/api/trips/routes/{r.Id}", new TripRouteDto(r.Id, r.Name, r.From, r.To, r.Kilometres, r.Purpose, r.Kind, r.CustomerId, r.ProjectId, r.Billable));
        });

        routes.MapDelete("/{id:int}", async (AppDb db, int id) =>
        {
            var r = await db.TripRoutes.FindAsync(id);
            if (r is null) return Results.NotFound();
            db.TripRoutes.Remove(r);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }
}

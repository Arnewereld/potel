using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Integrations;
using Potel.Api.Invoicing;
using Potel.Api.Payments;
using Potel.Api.Workflows;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration.GetValue<string>("DatabasePath") ?? "potel.db";
builder.Services.AddScoped<Tenant>();
builder.Services.AddSingleton<BusinessClock>();
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={dbPath}"));
// Back-ups van de database: elke dag, en altijd voor een update met nieuwe migraties (zie Database.Prepare).
builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));
builder.Services.AddSingleton(sp => new DatabaseBackups(dbPath, sp.GetRequiredService<IOptions<BackupOptions>>().Value,
    sp.GetRequiredService<ILogger<DatabaseBackups>>(), sp.GetRequiredService<BusinessClock>()));
if (builder.Configuration.GetValue("Backup:Daily", true))
    builder.Services.AddHostedService<BackupScheduler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "potel.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.ExpireTimeSpan = TimeSpan.FromDays(7);
        o.SlidingExpiration = true;
        // Een API stuurt geen redirect naar een loginpagina maar een statuscode.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx =>
        {
            ctx.Response.StatusCode = 403;
            return ctx.Response.WriteAsJsonAsync(new { error = "Dit mag alleen een beheerder." });
        };
        // Zet de werkruimte voor dit verzoek. Een uitgeschakelde of verwijderde gebruiker raakt direct zijn toegang kwijt,
        // net als een cookie van voor een nieuw wachtwoord, e-mailadres of andere rol: de rol komt altijd uit de database.
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
            if (ctx.Principal?.UserId() is not { } id || ctx.Principal.WorkspaceId() is not { } ws) { ctx.RejectPrincipal(); return; }
            db.Tenant.WorkspaceId = ws;
            var u = await db.Users.FindAsync(id);
            // Cookies van voor de stempel bestonden hebben er geen; die horen bij een lege stempel.
            var stamp = ctx.Principal.FindFirstValue(AuthEndpoints.StampClaim) ?? "";
            if (u is { Active: true } && u.SecurityStamp == stamp && ctx.Principal.FindFirstValue(ClaimTypes.Role) == u.Role)
            {
                // Verplicht de werkruimte inloggen in twee stappen voor beheerders, dan kan deze beheerder eerst alleen dat instellen.
                if (await TwoFactor.SetupRequiredAsync(db, u)) ctx.HttpContext.Items[TwoFactor.SetupRequiredItem] = true;
                return;
            }
            db.Tenant.WorkspaceId = 0;
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        };
        if (!builder.Environment.IsDevelopment()) o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });
builder.Services.AddAuthorization();

// Inloggen en aanmelden zijn per IP-adres begrensd, tegen het raden van wachtwoorden. Mislukte inlogpogingen
// tellen ook per e-mailadres en IP-adres (LoginThrottle), en nieuwe werkruimtes per netwerk en voor het hele platform
// (SignupThrottle). Gebruikers toevoegen of wijzigen is per werkruimte begrensd, net als het aantal werkstroom-runs
// per minuut (WorkflowRunBudget, voor handmatig, triggers en de planner samen).
var authPerMinute = builder.Configuration.GetValue("RateLimit:AuthPerMinute", 10);
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<SignupThrottle>();
builder.Services.AddSingleton<WorkflowRunBudget>();
// Inloggen in twee stappen (TOTP), met het geheim versleuteld via Data Protection.
builder.Services.AddSingleton<TwoFactor>();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, ct) => new ValueTask(ctx.HttpContext.Response.WriteAsJsonAsync(
        new { error = "Even rustig aan: dat waren te veel pogingen. Probeer het over een minuut opnieuw." }, ct));
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "onbekend",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("accounts", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.WorkspaceId() is { } ws ? $"ws:{ws}" : $"ip:{http.Connection.RemoteIpAddress}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = authPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // De webhook van Mollie is openbaar; per IP-adres begrensd, zodat niemand ons met verzonnen ids Mollie laat bestoken.
    var webhooksPerMinute = builder.Configuration.GetValue("RateLimit:MollieWebhookPerMinute", 120);
    o.AddPolicy("mollie", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "onbekend",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = webhooksPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // De openbare factuurpagina (link in een mail): per IP-adres begrensd, zodat niemand tokens kan blijven raden.
    var publicPerMinute = builder.Configuration.GetValue("RateLimit:PublicInvoicePerMinute", 30);
    o.AddPolicy("public", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "onbekend",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = publicPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // Betaal nu met iDEAL op de openbare factuurpagina: elke klik maakt een betaling bij Mollie, dus per IP-adres krap begrensd.
    var payPerMinute = builder.Configuration.GetValue("RateLimit:InvoicePayPerMinute", 10);
    o.AddPolicy("pay", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "onbekend",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = payPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // Het klantportaal (geheime link per klant): ook per IP-adres begrensd, tegen het raden van links.
    var portalPerMinute = builder.Configuration.GetValue("RateLimit:PortalPerMinute", 30);
    o.AddPolicy("portal", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "onbekend",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = portalPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

// Sleutels voor de inlogcookies bewaren, zodat gebruikers ingelogd blijven na een herstart of update.
if (builder.Configuration["KeysPath"] is { Length: > 0 } keysPath)
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath)).SetApplicationName("Potel");

builder.Services.Configure<WorkflowLimits>(builder.Configuration.GetSection("Workflows"));
builder.Services.AddScoped<WorkflowEngine>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
// Mails over je account (wachtwoord herstellen, adres bevestigen), met links naar het openbare adres uit App:BaseUrl.
builder.Services.AddSingleton<AppUrls>();
builder.Services.AddSingleton<AccountMailThrottle>();
builder.Services.AddSingleton<SystemMailQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SystemMailQueue>());
builder.Services.AddScoped<AccountMail>();
// Webhooks alleen naar openbare adressen, zonder doorverwijzingen of gedeelde cookies (zie WebhookGuard).
builder.Services.AddHttpClient(WebhookGuard.ClientName)
    .ConfigureHttpClient((sp, c) =>
    {
        c.Timeout = TimeSpan.FromSeconds(Math.Max(1, sp.GetRequiredService<IOptions<WorkflowLimits>>().Value.WebhookTimeoutSeconds));
        c.MaxResponseContentBufferSize = 64 * 1024;
    })
    .ConfigurePrimaryHttpMessageHandler(sp => WebhookGuard.CreateHandler(sp.GetRequiredService<IOptions<WorkflowLimits>>().Value.AllowPrivateWebhooks));
// Online betalen via Mollie, alleen als Mollie:ApiKey is ingesteld.
builder.Services.Configure<MollieOptions>(builder.Configuration.GetSection("Mollie"));
builder.Services.AddHttpClient<MollieClient>(c =>
{
    c.BaseAddress = new Uri(MollieClient.BaseUrl);
    c.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddScoped<MollieBilling>();
// iDEAL op facturen via de eigen Mollie van een werkruimte (een sleutel per werkruimte, los van Mollie:ApiKey).
builder.Services.AddScoped<InvoiceIdeal>();
// Urenvoorstellen uit GitHub: alleen naar api.github.com, met een tijdslimiet, een maximale grootte en zonder doorverwijzingen.
builder.Services.AddHttpClient<GitHubClient>(c =>
{
    c.Timeout = GitHubClient.Timeout;
    c.MaxResponseContentBufferSize = GitHubClient.MaxResponseBytes;
}).ConfigurePrimaryHttpMessageHandler(GitHubClient.CreateHandler);
// Terugkerende facturen. De planner hieronder zet ze per werkruimte klaar (IWorkspaceJob).
builder.Services.Configure<InvoicingLimits>(builder.Configuration.GetSection("Invoicing"));
builder.Services.AddScoped<RecurringInvoiceService>();
// Facturen en herinneringen mailen, met een openbare link naar de factuur. De planner stuurt de automatische herinneringen.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<InvoiceMailer>();
builder.Services.AddScoped<QuoteNotices>();
builder.Services.AddScoped<ReminderService>();
builder.Services.AddScoped<IWorkspaceJob, RecurringInvoiceJob>();
// Vaste kosten (abonnementen): elke periode één uitgave als concept, of alleen een herinnering.
builder.Services.AddScoped<RecurringExpenseService>();
builder.Services.AddScoped<IWorkspaceJob, RecurringExpenseJob>();
builder.Services.AddScoped<IWorkspaceJob, ReminderJob>();
if (builder.Configuration.GetValue("Workflows:Scheduler", true))
    builder.Services.AddHostedService<WorkflowScheduler>();

// Achter een reverse proxy (Caddy, Nginx, een hostingplatform) het echte IP-adres en https herkennen.
// Alleen de proxy direct voor de app telt, en alleen als hij bekend is: localhost, of wat in KnownProxies
// (IP-adressen) en KnownNetworks (bijvoorbeeld 172.16.0.0/12 voor Docker) staat. Anders kan iedereen een IP-adres verzinnen.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    foreach (var ip in builder.Configuration.GetSection("KnownProxies").Get<string[]>() ?? [])
        o.KnownProxies.Add(IPAddress.Parse(ip.Trim()));
    foreach (var cidr in builder.Configuration.GetSection("KnownNetworks").Get<string[]>() ?? [])
        o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr.Trim()));
});

var app = builder.Build();

if (app.Configuration.GetValue("BehindProxy", false)) app.UseForwardedHeaders();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    Database.Prepare(db, logger, app.Services.GetRequiredService<DatabaseBackups>());
    // Op de server: "dotnet Potel.Api.dll platform-admin jij@example.com" maakt dat account platformbeheerder en stopt dan.
    if (args is [Seed.PlatformAdminCommand, var platformEmail])
    {
        Environment.ExitCode = Seed.MakePlatformAdmin(db, platformEmail, logger) ? 0 : 1;
        return;
    }
    // "dotnet Potel.Api.dll reset-two-factor jij@example.com": telefoon en herstelcodes kwijt, dan zet je het zo uit op de server.
    if (args is [Seed.ResetTwoFactorCommand, var resetEmail])
    {
        Environment.ExitCode = Seed.ResetTwoFactor(db, resetEmail, logger) ? 0 : 1;
        return;
    }
    Seed.Bootstrap(db, app.Configuration, logger);
    Seed.PlatformAdmins(db, logger);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// De openbare factuur- en offertepagina en het klantportaal horen niet in zoekmachines en geven hun adres niet door aan andere sites.
app.Use(async (http, next) =>
{
    if (http.Request.Path.StartsWithSegments(InvoiceMailer.LinkPath) || http.Request.Path.StartsWithSegments(CustomerPortalEndpoints.LinkPath)
        || http.Request.Path.StartsWithSegments(QuoteEndpoints.LinkPath))
        InvoiceMailEndpoints.NoIndex(http.Response);
    await next();
});

// Als de frontend gebouwd is (npm run build) staat hij in wwwroot en serveert de API hem mee, met cacheregels per soort bestand.
var appShell = AppShell.Options();
app.UseDefaultFiles();
app.UseStaticFiles(appShell);

app.UseAuthentication();

// Een beheerder die inloggen in twee stappen nog moet instellen (verplicht in zijn werkruimte), kan alleen dat doen, uitloggen
// en openbare pagina's gebruiken. Al het andere onder /api wacht tot het is ingesteld.
app.Use(async (http, next) =>
{
    if (http.Items.ContainsKey(TwoFactor.SetupRequiredItem) && http.Request.Path.StartsWithSegments("/api") && !TwoFactor.AllowedBeforeSetup(http.Request))
    {
        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        await http.Response.WriteAsJsonAsync(new { error = TwoFactor.SetupRequiredMessage, twoFactorSetupRequired = true });
        return;
    }
    await next();
});

app.UseAuthorization();
app.UseRateLimiter();

// Na een verlopen proefperiode, of als een abonnement is afgelopen of niet betaald, kun je nog alles bekijken en exporteren,
// maar niets meer wijzigen tot je (weer) een abonnement kiest. Wel mag je nog inloggen, je wachtwoord wijzigen of herstellen,
// je e-mailadres bevestigen en inloggen in twee stappen regelen (alles onder /api/auth), betalen of opzeggen (/api/billing),
// gebruikers uitschakelen of verwijderen of hun inloggen in twee stappen uitzetten, dat verplicht maken voor beheerders,
// een link naar het klantportaal intrekken, het GitHub-token of de sleutel van Mollie weghalen, een offerte of de links ernaar intrekken,
// de welkomstwizard sluiten, je eigen lopende timer weggooien (er wordt dan niets geboekt) en je werkruimte verwijderen. Openbare
// pagina's (/api/public) werken gewoon, ook akkoord op een offerte. Al het andere is dicht, ook wat hier later bijkomt: een timer
// starten, pauzeren of stoppen en boeken kan dus pas weer met een abonnement. Een lopende timer blijft tot dan gewoon staan.
static bool WritableAfterTrial(HttpRequest r)
{
    if (r.Path.StartsWithSegments("/api/auth") || r.Path.StartsWithSegments("/api/platform") || r.Path.StartsWithSegments("/api/billing")) return true;
    // Openbare pagina's zonder inloggen, zoals een factuur via de link in een mail; ook als je toevallig ingelogd bent.
    if (r.Path.StartsWithSegments("/api/public")) return true;
    if (r.Path.StartsWithSegments("/api/users", out var user) && user.HasValue && (HttpMethods.IsPut(r.Method) || HttpMethods.IsDelete(r.Method))) return true;
    if (CustomerPortalEndpoints.IsRevoke(r)) return true;
    if (GitHubEndpoints.IsRemoveToken(r)) return true;
    if (InvoiceIdealEndpoints.IsRemoveKey(r)) return true;
    if (QuoteEndpoints.IsSafetyAction(r)) return true;
    if (TimerEndpoints.IsDiscard(r)) return true;
    if (r.Path.StartsWithSegments("/api/workspace", out var rest))
        return (HttpMethods.IsDelete(r.Method) && (!rest.HasValue || rest == "/"))
               || (HttpMethods.IsPost(r.Method) && rest.Equals("/onboarded", StringComparison.OrdinalIgnoreCase))
               || (HttpMethods.IsPut(r.Method) && rest.Equals("/two-factor", StringComparison.OrdinalIgnoreCase));
    return false;
}

app.Use(async (http, next) =>
{
    if (!HttpMethods.IsGet(http.Request.Method) && http.Request.Path.StartsWithSegments("/api")
        && !WritableAfterTrial(http.Request) && http.User.Identity?.IsAuthenticated == true)
    {
        var db = http.RequestServices.GetRequiredService<AppDb>();
        if (await db.Workspaces.FindAsync(db.TenantId) is { } w && w.ReadOnlyReason(DateTime.UtcNow) is { } reason)
        {
            http.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            await http.Response.WriteAsJsonAsync(new { error = reason });
            return;
        }
    }
    await next();
});

// Alles onder /api vraagt om een ingelogde gebruiker, behalve inloggen zelf.
var api = app.MapGroup("/api").RequireAuthorization();
api.MapAuth();
api.MapAccountEmail();
api.MapTwoFactor();
api.MapPublicInfo();
api.MapUsers();
api.MapDashboard();
api.MapCustomers();
api.MapCustomerPortal();
api.MapGitHub();
api.MapLeads();
api.MapInvoices();
api.MapRecurringInvoices();
api.MapInvoiceMail();
api.MapInvoiceIdeal();
api.MapQuotes();
api.MapExpenses();
api.MapRecurringExpenses();
api.MapBank();
api.MapTax();
api.MapTaxReserve();
api.MapInsights();
api.MapAppointments();
api.MapModules();
api.MapWorkflows();
api.MapProjects();
api.MapTime();
api.MapTimer();
api.MapTrips();
api.MapInvestments();
api.MapSettings();
api.MapWorkspace();
api.MapPlatform();
api.MapBilling();
api.MapFallback(() => Results.NotFound());

app.MapFallbackToFile("index.html", appShell);

app.Run();

public partial class Program;

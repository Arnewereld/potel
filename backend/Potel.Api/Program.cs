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
using Potel.Api.Workflows;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration.GetValue<string>("DatabasePath") ?? "potel.db";
builder.Services.AddScoped<Tenant>();
builder.Services.AddSingleton<BusinessClock>();
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={dbPath}"));
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
            if (u is { Active: true } && u.SecurityStamp == stamp && ctx.Principal.FindFirstValue(ClaimTypes.Role) == u.Role) return;
            db.Tenant.WorkspaceId = 0;
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        };
        if (!builder.Environment.IsDevelopment()) o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });
builder.Services.AddAuthorization();

// Inloggen en aanmelden zijn per IP-adres begrensd, tegen het raden van wachtwoorden. Mislukte inlogpogingen
// tellen ook per e-mailadres (LoginThrottle). Gebruikers toevoegen of wijzigen en werkstromen handmatig uitvoeren
// zijn per werkruimte begrensd.
var authPerMinute = builder.Configuration.GetValue("RateLimit:AuthPerMinute", 10);
var runsPerMinute = builder.Configuration.GetValue("RateLimit:WorkflowRunsPerMinute", 30);
builder.Services.AddSingleton<LoginThrottle>();
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
    o.AddPolicy("workflow-runs", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.WorkspaceId() is { } ws ? $"ws:{ws}" : $"ip:{http.Connection.RemoteIpAddress}",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = runsPerMinute, Window = TimeSpan.FromMinutes(1) }));
});

// Sleutels voor de inlogcookies bewaren, zodat gebruikers ingelogd blijven na een herstart of update.
if (builder.Configuration["KeysPath"] is { Length: > 0 } keysPath)
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath)).SetApplicationName("Potel");

builder.Services.Configure<WorkflowLimits>(builder.Configuration.GetSection("Workflows"));
builder.Services.AddScoped<WorkflowEngine>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
// Webhooks alleen naar openbare adressen, zonder doorverwijzingen of gedeelde cookies (zie WebhookGuard).
builder.Services.AddHttpClient(WebhookGuard.ClientName)
    .ConfigureHttpClient((sp, c) =>
    {
        c.Timeout = TimeSpan.FromSeconds(Math.Max(1, sp.GetRequiredService<IOptions<WorkflowLimits>>().Value.WebhookTimeoutSeconds));
        c.MaxResponseContentBufferSize = 64 * 1024;
    })
    .ConfigurePrimaryHttpMessageHandler(sp => WebhookGuard.CreateHandler(sp.GetRequiredService<IOptions<WorkflowLimits>>().Value.AllowPrivateWebhooks));
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
    Database.Prepare(db, logger);
    Seed.Bootstrap(db, app.Configuration, logger);
    Seed.PlatformAdmins(db, app.Configuration, logger);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Als de frontend gebouwd is (npm run build) staat hij in wwwroot en serveert de API hem mee.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Na een verlopen proefperiode kun je nog alles bekijken en exporteren, maar niets meer wijzigen tot je een abonnement kiest.
// Wel mag je nog inloggen, je wachtwoord wijzigen, gebruikers uitschakelen of verwijderen, de welkomstwizard sluiten
// en je werkruimte verwijderen. Al het andere is dicht, ook wat hier later bijkomt.
static bool WritableAfterTrial(HttpRequest r)
{
    if (r.Path.StartsWithSegments("/api/auth") || r.Path.StartsWithSegments("/api/platform")) return true;
    if (r.Path.StartsWithSegments("/api/users", out var user) && user.HasValue && (HttpMethods.IsPut(r.Method) || HttpMethods.IsDelete(r.Method))) return true;
    if (r.Path.StartsWithSegments("/api/workspace", out var rest))
        return (HttpMethods.IsDelete(r.Method) && (!rest.HasValue || rest == "/"))
               || (HttpMethods.IsPost(r.Method) && rest.Equals("/onboarded", StringComparison.OrdinalIgnoreCase));
    return false;
}

app.Use(async (http, next) =>
{
    if (!HttpMethods.IsGet(http.Request.Method) && http.Request.Path.StartsWithSegments("/api")
        && !WritableAfterTrial(http.Request) && http.User.Identity?.IsAuthenticated == true)
    {
        var db = http.RequestServices.GetRequiredService<AppDb>();
        if (await db.Workspaces.FindAsync(db.TenantId) is { } w && w.TrialExpired(DateTime.UtcNow))
        {
            http.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            await http.Response.WriteAsJsonAsync(new { error = Plans.TrialEndedError });
            return;
        }
    }
    await next();
});

// Alles onder /api vraagt om een ingelogde gebruiker, behalve inloggen zelf.
var api = app.MapGroup("/api").RequireAuthorization();
api.MapAuth();
api.MapUsers();
api.MapDashboard();
api.MapCustomers();
api.MapLeads();
api.MapInvoices();
api.MapAppointments();
api.MapModules();
api.MapWorkflows();
api.MapProjects();
api.MapTime();
api.MapSettings();
api.MapWorkspace();
api.MapPlatform();
api.MapFallback(() => Results.NotFound());

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;

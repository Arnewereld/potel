using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Workflows;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration.GetValue<string>("DatabasePath") ?? "potel.db";
builder.Services.AddScoped<Tenant>();
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
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = 403; return Task.CompletedTask; };
        // Zet de werkruimte voor dit verzoek. Een uitgeschakelde of verwijderde gebruiker raakt direct zijn toegang kwijt.
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
            if (ctx.Principal?.UserId() is not { } id || ctx.Principal.WorkspaceId() is not { } ws) { ctx.RejectPrincipal(); return; }
            db.Tenant.WorkspaceId = ws;
            if (await db.Users.FindAsync(id) is not { Active: true }) { db.Tenant.WorkspaceId = 0; ctx.RejectPrincipal(); }
        };
        if (!builder.Environment.IsDevelopment()) o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    });
builder.Services.AddAuthorization();

// Inloggen en aanmelden zijn per IP-adres begrensd, tegen het raden van wachtwoorden.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "onbekend",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimit:AuthPerMinute", 10), Window = TimeSpan.FromMinutes(1) }));
});

// Sleutels voor de inlogcookies bewaren, zodat gebruikers ingelogd blijven na een herstart of update.
if (builder.Configuration["KeysPath"] is { Length: > 0 } keysPath)
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath)).SetApplicationName("Potel");

builder.Services.AddScoped<WorkflowEngine>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(10));
if (builder.Configuration.GetValue("Workflows:Scheduler", true))
    builder.Services.AddHostedService<WorkflowScheduler>();

// Achter een reverse proxy (Caddy, Nginx, een hostingplatform) het echte IP-adres en https herkennen.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

if (app.Configuration.GetValue("BehindProxy", false)) app.UseForwardedHeaders();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    Database.Prepare(db, logger);
    Seed.Bootstrap(db, app.Configuration, logger);
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
string[] alwaysWritable = ["/api/auth", "/api/workspace", "/api/platform"];
app.Use(async (http, next) =>
{
    if (!HttpMethods.IsGet(http.Request.Method) && http.Request.Path.StartsWithSegments("/api")
        && !alwaysWritable.Any(p => http.Request.Path.StartsWithSegments(p)) && http.User.Identity?.IsAuthenticated == true)
    {
        var db = http.RequestServices.GetRequiredService<AppDb>();
        if (await db.Workspaces.FindAsync(db.TenantId) is { Plan: Plans.Trial, TrialEndsAt: { } end } && end < DateTime.UtcNow)
        {
            http.Response.StatusCode = StatusCodes.Status402PaymentRequired;
            await http.Response.WriteAsJsonAsync(new { error = "Je proefperiode is afgelopen. Kies een abonnement onder Instellingen om weer te kunnen werken." });
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

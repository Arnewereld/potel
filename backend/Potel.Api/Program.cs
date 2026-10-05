using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Workflows;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration.GetValue<string>("DatabasePath") ?? "potel.db";
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
        // Een uitgeschakelde of verwijderde gebruiker raakt direct zijn toegang kwijt.
        o.Events.OnValidatePrincipal = async ctx =>
        {
            var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDb>();
            if (ctx.Principal?.UserId() is not { } id || await db.Users.FindAsync(id) is not { Active: true })
                ctx.RejectPrincipal();
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddScoped<WorkflowEngine>();
builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
builder.Services.AddHttpClient("webhooks", c => c.Timeout = TimeSpan.FromSeconds(10));
if (builder.Configuration.GetValue("Workflows:Scheduler", true))
    builder.Services.AddHostedService<WorkflowScheduler>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    Database.Prepare(db, logger);
    Seed.EnsureAdmin(db, app.Configuration, logger);
    if (app.Configuration.GetValue("SeedDemoData", true)) Seed.Run(db);
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
api.MapFallback(() => Results.NotFound());

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;

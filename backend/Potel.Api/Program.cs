using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;
using Potel.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

var dbPath = builder.Configuration.GetValue<string>("DatabasePath") ?? "potel.db";
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
    Seed.Run(db);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();

// Als de frontend gebouwd is (npm run build) staat hij in wwwroot en serveert de API hem mee.
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");
api.MapDashboard();
api.MapCustomers();
api.MapLeads();
api.MapInvoices();
api.MapAppointments();
api.MapModules();
api.MapWorkflows();

app.MapFallbackToFile("index.html");

app.Run();

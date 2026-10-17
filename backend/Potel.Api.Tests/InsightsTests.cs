using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// De klok staat op donderdag 15 oktober 2026: van oktober zijn 15 dagen voorbij.
public class InsightsFactory : PortalFactory
{
    public static readonly DateTime Start = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 15, 10, 0, 0), BusinessClock.Zone);
    public MovableClock Clock { get; } = new(Start);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<BusinessClock>(Clock));
    }
}

// Wat je echt verdient: het effectieve uurtarief over alle uren, per klant en per project, een vaste prijs tegen je normale
// tarief, de omzet per maand met een jaar eerder en hoe afhankelijk je bent van één klant. Plus rollen, werkruimtes en alleen-lezen.
public class InsightsTests(InsightsFactory factory) : IClassFixture<InsightsFactory>
{
    static decimal Dec(JsonElement e, string name) => e.GetProperty(name).GetDecimal();
    static int Int(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    static Task<JsonElement> InsightsAsync(HttpClient c, string query = "") => c.GetFromJsonAsync<JsonElement>($"/api/insights{query}");

    async Task<(HttpClient C, int Ws, int Customer)> WorkspaceAsync(string prefix)
    {
        factory.Clock.Value = InsightsFactory.Start;
        var c = await ReadyAsync(factory, prefix);
        return (c, await TestApi.WorkspaceIdAsync(c), await CustomerAsync(c));
    }

    static async Task<int> EntryAsync(HttpClient c, int projectId, int minutes, string date, bool billable = true)
    {
        var res = await c.PostAsJsonAsync("/api/time", new { projectId, date, minutes, description = "Werk", billable });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    static async Task<int> InvoiceHoursAsync(HttpClient c, int projectId, params int[] entryIds)
    {
        var res = await c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { entryIds, detailed = false });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    // Een creditnota voor een deel van de factuur: het aantal (negatief) maal de prijs.
    internal static async Task<int> CreditAsync(HttpClient c, int invoiceId, decimal quantity, decimal unitPrice, string unit = "stuk")
    {
        var credit = await (await c.PostAsync($"/api/invoices/{invoiceId}/credit", null)).JsonAsync();
        var id = credit.GetProperty("id").GetInt32();
        var put = await c.PutAsJsonAsync($"/api/invoices/{id}", new
        {
            customerId = credit.GetProperty("customerId").GetInt32(), issueDate = credit.GetProperty("issueDate").GetString(),
            dueDate = credit.GetProperty("dueDate").GetString(), status = "concept", vatRegime = credit.GetProperty("vatRegime").GetString(),
            deliveryFrom = credit.GetProperty("deliveryFrom").GetString() ?? credit.GetProperty("issueDate").GetString(),
            lines = new[] { new { description = "Correctie", quantity, unit, unitPrice, vatRate = 21 } },
        });
        Assert.True(put.StatusCode == HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        var sent = await SetStatusAsync(c, id, "verzonden");
        Assert.True(sent.StatusCode == HttpStatusCode.OK, await sent.Content.ReadAsStringAsync());
        return id;
    }

    void SetIssueDate(int ws, int invoiceId, DateTime date) =>
        WorkflowKit.InWorkspace(factory, ws, db => db.Invoices.Where(i => i.Id == invoiceId).ExecuteUpdate(x => x.SetProperty(i => i.IssueDate, date)));

    [Fact]
    public async Task Effective_rate_counts_all_hours_and_credit_notes_subtract()
    {
        var (c, _, customer) = await WorkspaceAsync("inzicht-tarief");
        var project = await ProjectAsync(c, customer, 100);
        var invoiced = await EntryAsync(c, project, 600, "2026-10-05");
        await EntryAsync(c, project, 300, "2026-10-06", billable: false);
        var onDraft = await EntryAsync(c, project, 60, "2026-10-07");
        await EntryAsync(c, project, 180, "2026-10-31");                   // de laatste dag van de maand telt mee
        await EntryAsync(c, project, 120, "2026-09-30");                   // de dag ervoor niet
        await EntryAsync(c, project, 60, "2026-11-01");                    // en de dag erna ook niet

        // 10 uur gefactureerd (1.000) en 2 uur daarvan gecrediteerd (200). Het uur op een concept is nog niet gefactureerd.
        var sent = await InvoiceHoursAsync(c, project, invoiced);
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, sent, "verzonden")).StatusCode);
        await CreditAsync(c, sent, -2, 100, "uur");
        await InvoiceHoursAsync(c, project, onDraft);

        var month = await InsightsAsync(c, "?period=maand");
        Assert.Equal("maand", month.GetProperty("period").GetString());
        Assert.StartsWith("2026-10-01", month.GetProperty("from").GetString());
        Assert.StartsWith("2026-10-31", month.GetProperty("to").GetString());
        Assert.Equal("oktober 2026", month.GetProperty("label").GetString());
        Assert.Equal(800m, Dec(month, "revenue"));
        Assert.Equal(1140, Int(month, "minutes"));
        Assert.Equal(840, Int(month, "billableMinutes"));
        Assert.Equal(42.11m, Dec(month, "effectiveRate"));                  // 800 / 19 uur
        Assert.Equal(100m, Dec(month, "billableRate"));
        Assert.Equal(0.7368m, Dec(month, "billableShare"));                 // 14 van 19 uur
        Assert.Equal(68.6m, Dec(month, "goalHours"));                       // 32 uur per week, 15 dagen
        Assert.Equal(0.2041m, Dec(month, "occupancy"));
        Assert.Equal(240, Int(month, "openMinutes"));                       // het uur op het concept en de 3 uur van 31 oktober
        Assert.Equal(400m, Dec(month, "openValue"));
        Assert.Equal(0m, Dec(month, "unassignedRevenue"));
        var row = Assert.Single(month.GetProperty("projects").EnumerateArray());
        Assert.Equal(800m, Dec(row, "revenue"));                            // de creditnota trekt af bij het project van de factuur
        Assert.Equal(1140, Int(row, "minutes"));
        Assert.Equal(400m, Dec(row, "openValue"));
        Assert.Equal(42.11m, Dec(row, "effectiveRate"));
        Assert.Equal(JsonValueKind.Null, row.GetProperty("earnedRate").ValueKind);
        var customerRow = Assert.Single(month.GetProperty("customers").EnumerateArray());
        Assert.Equal(customer, Int(customerRow, "id"));
        Assert.Equal("Klant B.V.", customerRow.GetProperty("name").GetString());
        Assert.Equal(800m, Dec(customerRow, "revenue"));
        Assert.Equal(240, Int(customerRow, "openMinutes"));

        // Periodegrenzen: alleen 30 september, en het kwartaal met 1 november erbij. Het doel telt alleen de dagen tot en met vandaag.
        var day = await InsightsAsync(c, "?period=eigen&from=2026-09-30&to=2026-09-30");
        Assert.Equal(120, Int(day, "minutes"));
        Assert.Equal(0m, Dec(day, "revenue"));
        Assert.Equal(0m, Dec(day, "effectiveRate"));
        Assert.Equal(4.6m, Dec(day, "goalHours"));
        var quarter = await InsightsAsync(c, "?period=kwartaal");
        Assert.Equal("Q4 2026", quarter.GetProperty("label").GetString());
        Assert.Equal(1200, Int(quarter, "minutes"));
        Assert.Equal(68.6m, Dec(quarter, "goalHours"));
        var future = await InsightsAsync(c, "?period=eigen&from=2026-11-01&to=2026-11-30");
        Assert.Equal(0m, Dec(future, "goalHours"));
        Assert.Equal(JsonValueKind.Null, future.GetProperty("occupancy").ValueKind);
        // Zonder periode: dit jaar.
        var year = await InsightsAsync(c);
        Assert.Equal("jaar", year.GetProperty("period").GetString());
        Assert.Equal(1320, Int(year, "minutes"));
        var last12 = await InsightsAsync(c, "?period=12-maanden");
        Assert.StartsWith("2025-11-01", last12.GetProperty("from").GetString());
        Assert.Equal("november 2025 t/m oktober 2026", last12.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Fixed_price_shows_the_rate_earned_so_far_and_warns_below_the_normal_rate()
    {
        var (c, ws, customer) = await WorkspaceAsync("inzicht-vast");
        var created = await c.PostAsJsonAsync("/api/projects", new { name = "Webshop", customerId = customer, status = "actief", billing = "vast", fixedPrice = 5000, hourlyRate = 0, color = "#000000" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var project = (await created.JsonAsync()).GetProperty("id").GetInt32();
        await TimeAsync(c, project, 600, "2026-09-10", count: 4);           // 40 uur in september

        var row = Assert.Single((await InsightsAsync(c)).GetProperty("projects").EnumerateArray());
        Assert.Equal("vast", row.GetProperty("billing").GetString());
        Assert.Equal(5000m, Dec(row, "fixedPrice"));
        Assert.Equal(2400, Int(row, "minutesTotal"));
        Assert.Equal(125m, Dec(row, "earnedRate"));
        Assert.Equal(95m, Dec(row, "normalRate"));                          // het standaardtarief uit de instellingen
        Assert.False(row.GetProperty("belowNormal").GetBoolean());
        Assert.Equal(0m, Dec(row, "openValue"));                            // een vaste prijs heeft geen open uren

        // Nog 20 uur in oktober: 5.000 over 60 uur is minder dan het normale tarief. In de maand staan alleen de uren van oktober,
        // maar wat je verdient telt alle uren.
        await TimeAsync(c, project, 600, "2026-10-02", count: 2);
        row = Assert.Single((await InsightsAsync(c, "?period=maand")).GetProperty("projects").EnumerateArray());
        Assert.Equal(1200, Int(row, "minutes"));
        Assert.Equal(3600, Int(row, "minutesTotal"));
        Assert.Equal(83.33m, Dec(row, "earnedRate"));
        Assert.True(row.GetProperty("belowNormal").GetBoolean());

        // Een factuur hangt niet aan een project. Komt hij uit een offerte met dit project, dan telt hij wel voor het project.
        var invoice = await InvoiceAsync(c, customer, "verzonden", 1, 5000, description: "Webshop");
        var month = await InsightsAsync(c, "?period=maand");
        Assert.Equal(5000m, Dec(month, "unassignedRevenue"));
        Assert.Equal(0m, Dec(Assert.Single(month.GetProperty("projects").EnumerateArray()), "revenue"));
        Assert.Equal(5000m, Dec(Assert.Single(month.GetProperty("customers").EnumerateArray()), "revenue"));
        WorkflowKit.InWorkspace(factory, ws, db =>
        {
            db.Quotes.Add(new Quote { CustomerId = customer, Number = "OFF-2026-001", Status = QuoteStatus.Accepted, ProjectId = project, InvoiceId = invoice });
            return db.SaveChanges();
        });
        month = await InsightsAsync(c, "?period=maand");
        Assert.Equal(0m, Dec(month, "unassignedRevenue"));
        row = Assert.Single(month.GetProperty("projects").EnumerateArray());
        Assert.Equal(5000m, Dec(row, "revenue"));
        Assert.Equal(250m, Dec(row, "effectiveRate"));                      // 5.000 over de 20 uur van oktober

        // Met een eigen tarief op het project telt dat als normaal tarief.
        WorkflowKit.InWorkspace(factory, ws, db => db.Projects.Where(p => p.Id == project).ExecuteUpdate(x => x.SetProperty(p => p.HourlyRate, 80m)));
        row = Assert.Single((await InsightsAsync(c, "?period=maand")).GetProperty("projects").EnumerateArray());
        Assert.Equal(80m, Dec(row, "normalRate"));
        Assert.False(row.GetProperty("belowNormal").GetBoolean());
    }

    [Fact]
    public async Task Demo_data_is_left_out_unless_asked_for()
    {
        var (c, ws, customer) = await WorkspaceAsync("inzicht-demo");
        var project = await ProjectAsync(c, customer, 100);
        await InvoiceAsync(c, customer, "verzonden", 10, 100);
        var demo = await InvoiceAsync(c, customer, "verzonden", 5, 100);
        await EntryAsync(c, project, 600, "2026-10-01");
        var demoEntry = await EntryAsync(c, project, 300, "2026-10-02");
        WorkflowKit.InWorkspace(factory, ws, db =>
        {
            db.Invoices.Where(i => i.Id == demo).ExecuteUpdate(x => x.SetProperty(i => i.IsDemo, true).SetProperty(i => i.Number, "VOORBEELD-0001"));
            return db.TimeEntries.Where(t => t.Id == demoEntry).ExecuteUpdate(x => x.SetProperty(t => t.IsDemo, true));
        });

        var without = await InsightsAsync(c, "?period=maand");
        Assert.True(without.GetProperty("demoData").GetBoolean());
        Assert.False(without.GetProperty("includesDemo").GetBoolean());
        Assert.Equal(1000m, Dec(without, "revenue"));
        Assert.Equal(600, Int(without, "minutes"));
        Assert.Equal(1000m, Dec(without.GetProperty("months")[11], "revenue"));
        Assert.Equal(1000m, Dec(without.GetProperty("concentration"), "revenue"));

        var with = await InsightsAsync(c, "?period=maand&demo=true");
        Assert.True(with.GetProperty("includesDemo").GetBoolean());
        Assert.Equal(1500m, Dec(with, "revenue"));
        Assert.Equal(900, Int(with, "minutes"));
        Assert.Equal(1500m, Dec(with.GetProperty("months")[11], "revenue"));
    }

    [Fact]
    public async Task Revenue_per_month_compares_with_a_year_earlier_and_shows_concentration()
    {
        var (c, ws, a) = await WorkspaceAsync("inzicht-maanden");
        var b = await CustomerAsync(c, "Tweede");
        SetIssueDate(ws, await InvoiceAsync(c, a, "verzonden", 10, 100), new DateTime(2025, 10, 10));   // een jaar eerder
        SetIssueDate(ws, await InvoiceAsync(c, a, "verzonden", 10, 100), new DateTime(2025, 10, 31));   // net buiten de 12 maanden
        await InvoiceAsync(c, a, "verzonden", 70, 100);                                                 // oktober 2026
        var forB = await InvoiceAsync(c, b, "verzonden", 20, 100);
        SetIssueDate(ws, forB, new DateTime(2026, 6, 1));
        SetIssueDate(ws, await CreditAsync(c, forB, -5, 100), new DateTime(2026, 7, 1));
        SetIssueDate(ws, await InvoiceAsync(c, b, "verzonden", 3, 100), new DateTime(2025, 11, 1));     // de eerste dag van de 12 maanden

        var data = await InsightsAsync(c, "?period=12-maanden");
        var months = data.GetProperty("months").EnumerateArray().ToList();
        Assert.Equal(12, months.Count);
        Assert.Equal("2025-11", months[0].GetProperty("month").GetString());
        Assert.Equal(300m, Dec(months[0], "revenue"));
        Assert.Equal("2026-10", months[11].GetProperty("month").GetString());
        Assert.Equal(7000m, Dec(months[11], "revenue"));
        Assert.Equal(2000m, Dec(months[11], "previous"));
        Assert.Equal(2000m, Dec(months[7], "revenue"));                     // juni
        Assert.Equal(-500m, Dec(months[8], "revenue"));                     // juli: alleen de creditnota
        Assert.Equal(8800m, Dec(data, "revenue"));

        // 7.000 van 8.800 bij één klant: ruim boven 70 procent.
        var spread = data.GetProperty("concentration");
        Assert.Equal(8800m, Dec(spread, "revenue"));
        Assert.Equal(0.7955m, Dec(spread, "topShare"));
        Assert.True(spread.GetProperty("warning").GetBoolean());
        var top = spread.GetProperty("customers")[0];
        Assert.Equal(a, Int(top, "customerId"));
        Assert.Equal(7000m, Dec(top, "revenue"));
        Assert.Equal(1800m, Dec(spread.GetProperty("customers")[1], "revenue"));

        // Precies 70 procent is nog geen waarschuwing.
        SetIssueDate(ws, await InvoiceAsync(c, b, "verzonden", 12, 100), new DateTime(2026, 9, 1));
        spread = (await InsightsAsync(c)).GetProperty("concentration");
        Assert.Equal(0.7m, Dec(spread, "topShare"));
        Assert.False(spread.GetProperty("warning").GetBoolean());
    }

    [Fact]
    public void A_credit_note_is_split_like_its_invoice_without_losing_a_cent()
    {
        // Drie keer 16,67 en 50 is een cent te veel: die cent gaat van het grootste deel af.
        var parts = new Dictionary<int, decimal> { [1] = 50m, [2] = 50m, [3] = 50m, [RevenueSplit.NoProject] = 150m, [4] = 0m };
        var split = RevenueSplit.Proportional(parts, 300m, -100m);
        Assert.Equal(-100m, split.Values.Sum());
        Assert.Equal(-16.67m, split[1]);
        Assert.Equal(-16.67m, split[3]);
        Assert.Equal(-49.99m, split[RevenueSplit.NoProject]);
        Assert.False(split.ContainsKey(4));
        Assert.Equal(-50m, RevenueSplit.Proportional([], 0m, -50m)[RevenueSplit.NoProject]);
    }

    [Fact]
    public async Task Periods_must_make_sense()
    {
        var (c, _, _) = await WorkspaceAsync("inzicht-periode");
        async Task<string> Refused(string query)
        {
            var res = await c.GetAsync($"/api/insights{query}");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return await res.ErrorAsync();
        }
        Assert.Contains("Kies een periode", await Refused("?period=week"));
        Assert.Contains("begin- en einddatum", await Refused("?period=eigen"));
        Assert.Contains("begin- en einddatum", await Refused("?period=eigen&from=2026-10-02&to=2026-10-01"));
        Assert.Contains("tien jaar", await Refused("?period=eigen&from=2010-01-01&to=2026-10-01"));
        Assert.Contains("begin- en einddatum", await Refused("?period=eigen&from=1999-01-01&to=1999-02-01"));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/insights?period=eigen&from=2026-01-01&to=2026-12-31")).StatusCode);
    }

    [Fact]
    public async Task Only_admins_see_insights_and_each_workspace_sees_its_own()
    {
        var (admin, ws, customer) = await WorkspaceAsync("inzicht-rollen");
        await InvoiceAsync(admin, customer, "verzonden", 10, 100);
        var employeeEmail = $"inzicht-medewerker-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(admin, employeeEmail);
        var employee = await TestApi.LoginAsync(factory, employeeEmail);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/insights")).StatusCode);

        var (other, _, _) = await WorkspaceAsync("inzicht-buur");
        var theirs = await InsightsAsync(other);
        Assert.Equal(0m, Dec(theirs, "revenue"));
        Assert.Empty(theirs.GetProperty("customers").EnumerateArray());
        Assert.Equal(1000m, Dec(await InsightsAsync(admin), "revenue"));

        // Alleen-lezen: bekijken mag gewoon.
        await TestApi.ExpireTrialAsync(factory, ws);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/insights?period=maand")).StatusCode);
    }
}

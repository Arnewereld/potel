using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Een klok die stilstaat op een vast moment, zodat we tijdzone en jaarwisseling kunnen testen.
public class FixedClock(DateTime utcNow) : BusinessClock
{
    public override DateTime UtcNow => utcNow;
}

// Oudejaarsavond 23:30 UTC: in Nederland is het dan al 1 januari 2027, 00:30.
public class NewYearInAmsterdamFactory : PortalFactory
{
    public static readonly DateTime UtcNow = new(2026, 12, 31, 23, 30, 0, DateTimeKind.Utc);

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<BusinessClock>(new FixedClock(UtcNow)));
    }
}

// Week, jaar, factuurdatums en verlopen facturen volgen de Nederlandse kalender, ook als de server op UTC draait.
public class AmsterdamCalendarTests(NewYearInAmsterdamFactory factory) : IClassFixture<NewYearInAmsterdamFactory>
{
    [Fact]
    public async Task Server_created_invoice_dates_use_the_dutch_date()
    {
        var c = await ReadyAsync(factory, "nieuwjaar-factuur");
        var customerId = await CustomerAsync(c);
        var projectId = await ProjectAsync(c, customerId, 95);
        await TimeAsync(c, projectId, 60, "2026-12-30");
        var res = await c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await Json(res)).GetProperty("id").GetInt32();
        var inv = await GetInvoiceAsync(c, id);
        Assert.StartsWith("2027-01-01", inv.GetProperty("issueDate").GetString());
        Assert.StartsWith("2027-01-15", inv.GetProperty("dueDate").GetString());

        var sent = await Json(await SetStatusAsync(c, id, "verzonden"));
        Assert.Equal("2027-0001", sent.GetProperty("number").GetString());
    }

    [Fact]
    public async Task Dashboard_week_and_year_follow_the_dutch_calendar()
    {
        var c = await ReadyAsync(factory, "nieuwjaar-uren");
        var customerId = await CustomerAsync(c);
        var projectId = await ProjectAsync(c, customerId, 95);
        await TimeAsync(c, projectId, 60, "2027-01-01");
        await TimeAsync(c, projectId, 120, "2026-12-31");
        await TimeAsync(c, projectId, 600, "2027-03-01");   // later dit jaar: telt nog niet mee
        await TimeAsync(c, projectId, 300, "2028-01-15");   // volgend jaar

        var hours = (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("hours");
        var byDay = hours.GetProperty("byDay");
        Assert.Equal(60, hours.GetProperty("year").GetInt32());
        Assert.Equal(180, hours.GetProperty("week").GetInt32());
        Assert.Equal("2026-12-28", byDay[0].GetProperty("date").GetString());
        Assert.Equal(120, byDay[3].GetProperty("minutes").GetInt32());
        Assert.Equal(60, byDay[4].GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task Invoice_due_yesterday_in_the_netherlands_is_overdue()
    {
        var c = await ReadyAsync(factory, "nieuwjaar-verlopen");
        var customerId = await CustomerAsync(c);
        var id = await InvoiceAsync(c, customerId, "verzonden", 1, 100, issueDate: "2026-12-17");   // vervalt 31 december

        var dash = await c.GetFromJsonAsync<JsonElement>("/api/dashboard");
        Assert.Equal(1, dash.GetProperty("overdue").GetInt32());
        Assert.Equal("verlopen", (await GetInvoiceAsync(c, id)).GetProperty("status").GetString());
    }

    [Fact]
    public void Zone_knows_dutch_winter_and_summer_time()
    {
        Assert.Equal(TimeSpan.FromHours(1), BusinessClock.Zone.GetUtcOffset(new DateTime(2027, 1, 15, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(TimeSpan.FromHours(2), BusinessClock.Zone.GetUtcOffset(new DateTime(2027, 7, 15, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2027, 1, 1, 0, 30, 0), new FixedClock(NewYearInAmsterdamFactory.UtcNow).Now);
    }
}

// Het urencriterium telt alleen uren van dit kalenderjaar tot en met vandaag.
public class UrencriteriumTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    [Fact]
    public async Task Year_counts_only_this_calendar_year_up_to_today()
    {
        var c = await ReadyAsync(factory, "urencriterium");
        var customerId = await CustomerAsync(c);
        var projectId = await ProjectAsync(c, customerId, 95);
        var today = new BusinessClock().Today;
        await TimeAsync(c, projectId, 60, Day(today));
        await TimeAsync(c, projectId, 600, Day(new DateTime(today.Year + 1, 1, 15)));

        var hours = (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("hours");
        Assert.Equal(60, hours.GetProperty("year").GetInt32());
    }
}

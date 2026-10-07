using System.Net;
using System.Text.Json;
using Potel.Api.Data;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Versturen geeft een factuur de datum van vandaag: een oud concept wordt niet teruggedateerd, is dus niet meteen verlopen,
// en de nummers lopen gelijk op met de datums. De betaaltermijn blijft even lang.
public class SendDateTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    static DateTime Date(JsonElement inv, string field) => DateTime.Parse(inv.GetProperty(field).GetString()!);

    [Fact]
    public async Task Sending_an_old_draft_dates_it_today_and_keeps_the_payment_term()
    {
        var c = await ReadyAsync(factory, "oud-concept");
        var customerId = await CustomerAsync(c);
        var today = new BusinessClock().Today;
        var old = await InvoiceAsync(c, customerId, "concept", 1, 100, issueDate: Day(today.AddDays(-30)));
        var fresh = await InvoiceAsync(c, customerId, "concept", 1, 100, issueDate: Day(today));

        var first = await Json(await SetStatusAsync(c, fresh, "verzonden"));
        var second = await Json(await SetStatusAsync(c, old, "verzonden"));

        Assert.Equal("verzonden", second.GetProperty("status").GetString());
        Assert.Equal(today, Date(second, "issueDate"));
        Assert.Equal(today.AddDays(14), Date(second, "dueDate"));
        Assert.True(Date(second, "issueDate") >= Date(first, "issueDate"));
        Assert.Equal($"{today.Year}-0001", first.GetProperty("number").GetString());
        Assert.Equal($"{today.Year}-0002", second.GetProperty("number").GetString());

        // Ook in de lijst (waar verlopen facturen worden bijgewerkt) blijft hij gewoon verzonden.
        var again = await GetInvoiceAsync(c, old);
        await c.GetAsync("/api/invoices");
        Assert.Equal("verzonden", (await GetInvoiceAsync(c, old)).GetProperty("status").GetString());
        Assert.Equal(today, Date(again, "issueDate"));
    }

    [Fact]
    public async Task A_draft_dated_in_the_future_gets_todays_date_as_well()
    {
        var c = await ReadyAsync(factory, "toekomst-concept");
        var customerId = await CustomerAsync(c);
        var today = new BusinessClock().Today;
        var later = await InvoiceAsync(c, customerId, "concept", 1, 100, issueDate: Day(today.AddDays(10)));
        var sent = await SetStatusAsync(c, later, "verzonden");
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var inv = await Json(sent);
        Assert.Equal(today, Date(inv, "issueDate"));
        Assert.Equal(today.AddDays(14), Date(inv, "dueDate"));
    }
}

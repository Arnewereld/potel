using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// De timer stuurt een eigen kenmerk mee; dezelfde timer twee keer stoppen geeft één boeking.
public class TimerBookingTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    static async Task<int> ProjectAsync(HttpClient c)
    {
        var customer = await CreateCustomerAsync(c);
        return (await (await c.PostAsJsonAsync("/api/projects", new { name = "App", customerId = customer, status = "actief", billing = "uur", hourlyRate = 95, color = "#4ea5ff" })).JsonAsync()).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Same_timer_sent_twice_at_once_is_booked_once()
    {
        var c = await RegisterAsync(factory, "timer-dubbel@example.com");
        var projectId = await ProjectAsync(c);
        var booking = new { projectId, date = "2026-10-06", minutes = 45, description = "Bouwen", billable = true, clientId = "timer-123" };
        var results = await Task.WhenAll(c.PostAsJsonAsync("/api/time", booking), c.PostAsJsonAsync("/api/time", booking));
        Assert.All(results, r => Assert.True(r.IsSuccessStatusCode));

        var again = await c.PostAsJsonAsync("/api/time", booking);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var entries = await c.GetFromJsonAsync<List<JsonElement>>($"/api/time?projectId={projectId}");
        Assert.Single(entries!);
        Assert.Equal(entries![0].GetProperty("id").GetInt32(), (await again.JsonAsync()).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Manual_bookings_without_a_timer_id_may_be_identical()
    {
        var c = await RegisterAsync(factory, "timer-handmatig@example.com");
        var projectId = await ProjectAsync(c);
        var booking = new { projectId, date = "2026-10-06", minutes = 45, description = "Bouwen", billable = true };
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/time", booking)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/time", booking)).StatusCode);
        Assert.Equal(2, (await c.GetFromJsonAsync<List<JsonElement>>($"/api/time?projectId={projectId}"))!.Count);
    }

    [Fact]
    public async Task Timer_ids_are_separate_per_workspace()
    {
        var a = await RegisterAsync(factory, "timer-ws-a@example.com");
        var b = await RegisterAsync(factory, "timer-ws-b@example.com");
        var pa = await ProjectAsync(a);
        var pb = await ProjectAsync(b);
        Assert.Equal(HttpStatusCode.Created, (await a.PostAsJsonAsync("/api/time", new { projectId = pa, date = "2026-10-06", minutes = 30, billable = true, clientId = "zelfde" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await b.PostAsJsonAsync("/api/time", new { projectId = pb, date = "2026-10-06", minutes = 30, billable = true, clientId = "zelfde" })).StatusCode);
        Assert.Single((await b.GetFromJsonAsync<List<JsonElement>>("/api/time"))!);
    }
}

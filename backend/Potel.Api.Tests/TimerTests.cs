using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

public class TimerFactory : PortalFactory
{
    public MovableClock Clock { get; } = new(new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<BusinessClock>(Clock));
    }

    // Een moment in Nederlandse tijd.
    public void At(int day, int hour, int minute = 0, int second = 0) =>
        Clock.Value = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, day, hour, minute, second, DateTimeKind.Unspecified), BusinessClock.Zone);

    public void Later(TimeSpan span) => Clock.Value += span;

    public int RunningTimers(string email)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var user = db.Users.IgnoreQueryFilters().Single(u => u.Email == email);
        return db.RunningTimers.IgnoreQueryFilters().Count(t => t.UserId == user.Id);
    }
}

// De timer staat op de server: één per gebruiker, op elk apparaat dezelfde, en stoppen boekt de tijd zoals de timer in de browser altijd deed.
public class TimerTests(TimerFactory factory) : IClassFixture<TimerFactory>
{
    static async Task<int> ProjectAsync(HttpClient c, string name = "App", string billing = "uur")
    {
        var customer = await CreateCustomerAsync(c);
        var res = await c.PostAsJsonAsync("/api/projects", new { name, customerId = customer, status = "actief", billing, hourlyRate = 95, color = "#4ea5ff" });
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    static async Task<JsonElement> StartAsync(HttpClient c, int projectId, string description = "Bouwen")
    {
        var res = await c.PostAsJsonAsync("/api/timer", new { projectId, description });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.JsonAsync()).GetProperty("timer");
    }

    static async Task<JsonElement> TimerAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonElement>("/api/timer")).GetProperty("timer");

    static string Key(JsonElement timer) => timer.GetProperty("key").GetString()!;

    static async Task<JsonElement> StopAsync(HttpClient c, string key, object? extra = null)
    {
        var res = await c.PostAsJsonAsync("/api/timer/stop", extra ?? new { key });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.JsonAsync();
    }

    [Fact]
    public async Task Two_devices_starting_at_once_get_one_running_timer()
    {
        factory.At(14, 9);
        var laptop = await RegisterAsync(factory, "timer-tegelijk@example.com");
        var phone = await LoginAsync(factory, "timer-tegelijk@example.com");
        var projectId = await ProjectAsync(laptop);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
            (i % 2 == 0 ? laptop : phone).PostAsJsonAsync("/api/timer", new { projectId, description = $"Poging {i}" })));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, factory.RunningTimers("timer-tegelijk@example.com"));
        // De geweigerde starts krijgen de timer die wel loopt mee, en beide apparaten zien dezelfde.
        var refused = await results.First(r => r.StatusCode == HttpStatusCode.Conflict).JsonAsync();
        Assert.Equal(Key(await TimerAsync(laptop)), Key(refused.GetProperty("timer")));
        Assert.Equal(Key(await TimerAsync(laptop)), Key(await TimerAsync(phone)));
    }

    [Fact]
    public async Task The_database_itself_refuses_a_second_timer_for_the_same_user()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "timer-index@example.com");
        var projectId = await ProjectAsync(c);
        await StartAsync(c, projectId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var first = db.RunningTimers.IgnoreQueryFilters().Single(t => t.ProjectId == projectId);
        db.Tenant.WorkspaceId = first.WorkspaceId;
        db.RunningTimers.Add(new RunningTimer { UserId = first.UserId, ProjectId = projectId, StartedAt = factory.Clock.Value, Key = "timer-tweede" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_timer_started_on_one_device_shows_up_on_another_with_the_server_time()
    {
        factory.At(14, 9);
        var laptop = await RegisterAsync(factory, "timer-apparaten@example.com");
        var projectId = await ProjectAsync(laptop, "Webshop");
        await StartAsync(laptop, projectId, "Inloggen bouwen");
        factory.Later(TimeSpan.FromMinutes(5));

        var phone = await LoginAsync(factory, "timer-apparaten@example.com");
        var state = await phone.GetFromJsonAsync<JsonElement>("/api/timer");
        var timer = state.GetProperty("timer");
        Assert.Equal("Webshop", timer.GetProperty("projectName").GetString());
        Assert.Equal("Inloggen bouwen", timer.GetProperty("description").GetString());
        Assert.Equal(300, timer.GetProperty("elapsedSeconds").GetInt32());
        Assert.Equal(factory.Clock.Value, state.GetProperty("serverNow").GetDateTime().ToUniversalTime());
        Assert.Equal(factory.Clock.Value.AddMinutes(-5), timer.GetProperty("startedAt").GetDateTime().ToUniversalTime());
        Assert.EndsWith("Z", timer.GetProperty("startedAt").GetString());
    }

    [Fact]
    public async Task Stop_books_the_rounded_minutes_on_the_start_date_like_before()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "timer-stop@example.com");
        var me = await MyIdAsync(c);
        var projectId = await ProjectAsync(c);
        var timer = await StartAsync(c, projectId, "  API koppelen  ");
        factory.Later(TimeSpan.FromSeconds(44 * 60 + 30));

        var stopped = await StopAsync(c, Key(timer));
        Assert.Equal(JsonValueKind.Null, stopped.GetProperty("timer").ValueKind);
        var entry = stopped.GetProperty("entry");
        Assert.Equal(45, entry.GetProperty("minutes").GetInt32());
        Assert.Equal(new DateTime(2026, 10, 14), entry.GetProperty("date").GetDateTime());
        Assert.Equal("API koppelen", entry.GetProperty("description").GetString());
        Assert.True(entry.GetProperty("billable").GetBoolean());
        Assert.Equal(me, entry.GetProperty("userId").GetInt32());
        Assert.Equal(Key(timer), entry.GetProperty("clientId").GetString());
        Assert.Equal(JsonValueKind.Null, (await TimerAsync(c)).ValueKind);
        Assert.Single((await c.GetFromJsonAsync<List<JsonElement>>($"/api/time?projectId={projectId}"))!);

        // Een paar seconden is toch één minuut, net als altijd.
        timer = await StartAsync(c, projectId);
        factory.Later(TimeSpan.FromSeconds(20));
        Assert.Equal(1, (await StopAsync(c, Key(timer))).GetProperty("entry").GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task A_fixed_price_project_starts_as_not_billable_unless_chosen()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "timer-vast@example.com");
        var fixedPrice = await ProjectAsync(c, "Vaste prijs", "vast");
        var timer = await StartAsync(c, fixedPrice);
        Assert.False(timer.GetProperty("billable").GetBoolean());
        var changed = await (await c.PutAsJsonAsync("/api/timer", new { key = Key(timer), billable = true, description = "Toch factureren" })).JsonAsync();
        Assert.True(changed.GetProperty("timer").GetProperty("billable").GetBoolean());
        factory.Later(TimeSpan.FromMinutes(10));
        var entry = (await StopAsync(c, Key(timer))).GetProperty("entry");
        Assert.True(entry.GetProperty("billable").GetBoolean());
        Assert.Equal("Toch factureren", entry.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Paused_time_is_not_booked()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "timer-pauze@example.com");
        var projectId = await ProjectAsync(c);
        var key = Key(await StartAsync(c, projectId));
        factory.Later(TimeSpan.FromMinutes(30));
        var paused = (await (await c.PostAsJsonAsync("/api/timer/pause", new { key })).JsonAsync()).GetProperty("timer");
        Assert.NotEqual(JsonValueKind.Null, paused.GetProperty("pausedAt").ValueKind);
        factory.Later(TimeSpan.FromMinutes(60));
        Assert.Equal(30 * 60, (await TimerAsync(c)).GetProperty("elapsedSeconds").GetInt32());
        // Twee keer pauzeren verschuift niets.
        (await c.PostAsJsonAsync("/api/timer/pause", new { key })).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync("/api/timer/resume", new { key })).EnsureSuccessStatusCode();
        factory.Later(TimeSpan.FromMinutes(15));
        Assert.Equal(45, (await StopAsync(c, key)).GetProperty("entry").GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task Switching_project_books_the_old_one_and_keeps_running_on_the_new_one()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "timer-wissel@example.com");
        var first = await ProjectAsync(c, "Eerste");
        var second = await ProjectAsync(c, "Tweede", "vast");
        var key = Key(await StartAsync(c, first, "Bugfix"));
        factory.Later(TimeSpan.FromMinutes(30));

        var res = await c.PostAsJsonAsync("/api/timer/switch", new { key, projectId = second, description = "Overleg" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var state = await res.JsonAsync();
        var entry = state.GetProperty("entry");
        Assert.Equal(first, entry.GetProperty("projectId").GetInt32());
        Assert.Equal(30, entry.GetProperty("minutes").GetInt32());
        Assert.Equal("Bugfix", entry.GetProperty("description").GetString());
        var next = state.GetProperty("timer");
        Assert.Equal(second, next.GetProperty("projectId").GetInt32());
        Assert.Equal("Overleg", next.GetProperty("description").GetString());
        Assert.False(next.GetProperty("billable").GetBoolean());
        Assert.Equal(0, next.GetProperty("elapsedSeconds").GetInt32());
        Assert.NotEqual(key, Key(next));

        // Naar hetzelfde project wisselen doet niets, en het oude kenmerk werkt niet meer op de nieuwe timer.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/timer/switch", new { key = Key(next), projectId = second })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/timer/switch", new { key, projectId = first })).StatusCode);

        factory.Later(TimeSpan.FromMinutes(10));
        Assert.Equal(10, (await StopAsync(c, Key(next))).GetProperty("entry").GetProperty("minutes").GetInt32());
        Assert.Equal(0, factory.RunningTimers("timer-wissel@example.com"));
    }

    [Fact]
    public async Task A_timer_left_running_past_midnight_asks_for_the_end_time()
    {
        factory.At(14, 16);
        var c = await RegisterAsync(factory, "timer-nacht@example.com");
        var projectId = await ProjectAsync(c);
        var key = Key(await StartAsync(c, projectId));
        factory.At(15, 9);

        Assert.True((await TimerAsync(c)).GetProperty("needsConfirmation").GetBoolean());
        var asked = await c.PostAsJsonAsync("/api/timer/stop", new { key });
        Assert.Equal(HttpStatusCode.Conflict, asked.StatusCode);
        var body = await asked.JsonAsync();
        Assert.True(body.GetProperty("needsConfirmation").GetBoolean());
        Assert.Equal(key, Key(body.GetProperty("timer")));
        Assert.Equal(1, factory.RunningTimers("timer-nacht@example.com"));
        Assert.Empty((await c.GetFromJsonAsync<List<JsonElement>>($"/api/time?projectId={projectId}"))!);

        // Meer dan 24 uur kan ook na bevestigen niet; een eindtijd in de toekomst of voor het begin ook niet.
        factory.At(15, 17);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/timer/stop", new { key, confirmed = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/timer/stop", new { key, endedAt = factory.Clock.Value.AddHours(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/timer/stop", new { key, endedAt = factory.Clock.Value.AddDays(-2) })).StatusCode);

        // Gisteren om half zes gestopt: anderhalf uur op de dag waarop hij begon.
        var endedAt = new DateTimeOffset(new DateTime(2026, 10, 14, 17, 30, 0), TimeSpan.FromHours(2));
        var entry = (await StopAsync(c, key, new { key, endedAt })).GetProperty("entry");
        Assert.Equal(90, entry.GetProperty("minutes").GetInt32());
        Assert.Equal(new DateTime(2026, 10, 14), entry.GetProperty("date").GetDateTime());
        Assert.Equal(0, factory.RunningTimers("timer-nacht@example.com"));
    }

    [Fact]
    public async Task More_than_twelve_hours_on_one_day_asks_first_and_can_then_be_booked()
    {
        factory.At(14, 6);
        var c = await RegisterAsync(factory, "timer-lang@example.com");
        var projectId = await ProjectAsync(c);
        var key = Key(await StartAsync(c, projectId));
        factory.At(14, 18);
        Assert.False((await TimerAsync(c)).GetProperty("needsConfirmation").GetBoolean());
        factory.At(14, 19);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/timer/stop", new { key })).StatusCode);
        // Wisselen van project vraagt hetzelfde.
        var other = await ProjectAsync(c, "Ander");
        var switching = await c.PostAsJsonAsync("/api/timer/switch", new { key, projectId = other });
        Assert.Equal(HttpStatusCode.Conflict, switching.StatusCode);
        Assert.True((await switching.JsonAsync()).GetProperty("needsConfirmation").GetBoolean());

        var entry = (await StopAsync(c, key, new { key, confirmed = true })).GetProperty("entry");
        Assert.Equal(13 * 60, entry.GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task Stopping_twice_at_once_books_once()
    {
        factory.At(14, 9);
        var laptop = await RegisterAsync(factory, "timer-dubbelstop@example.com");
        var phone = await LoginAsync(factory, "timer-dubbelstop@example.com");
        var projectId = await ProjectAsync(laptop);
        var key = Key(await StartAsync(laptop, projectId));
        factory.Later(TimeSpan.FromMinutes(25));

        var results = await Task.WhenAll(
            laptop.PostAsJsonAsync("/api/timer/stop", new { key }), phone.PostAsJsonAsync("/api/timer/stop", new { key }),
            laptop.PostAsJsonAsync("/api/timer/stop", new { key }), phone.PostAsJsonAsync("/api/timer/stop", new { key }));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var ids = new HashSet<int>();
        foreach (var r in results) ids.Add((await r.JsonAsync()).GetProperty("entry").GetProperty("id").GetInt32());
        Assert.Single(ids);
        Assert.Single((await laptop.GetFromJsonAsync<List<JsonElement>>($"/api/time?projectId={projectId}"))!);
    }

    [Fact]
    public async Task An_outdated_screen_never_stops_or_discards_a_newer_timer()
    {
        factory.At(14, 9);
        var laptop = await RegisterAsync(factory, "timer-oud@example.com");
        var phone = await LoginAsync(factory, "timer-oud@example.com");
        var projectId = await ProjectAsync(laptop);
        var old = Key(await StartAsync(laptop, projectId, "Oud"));
        factory.Later(TimeSpan.FromMinutes(20));
        await StopAsync(phone, old);
        var current = Key(await StartAsync(phone, projectId, "Nieuw"));
        factory.Later(TimeSpan.FromMinutes(5));

        // De laptop zag de oude timer nog: stoppen geeft de boeking die er al was, en de nieuwe timer loopt door.
        var stale = await StopAsync(laptop, old);
        Assert.Equal(20, stale.GetProperty("entry").GetProperty("minutes").GetInt32());
        Assert.Equal(current, Key(stale.GetProperty("timer")));

        Assert.Equal(HttpStatusCode.Conflict, (await laptop.DeleteAsync($"/api/timer?key={old}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await laptop.PostAsJsonAsync("/api/timer/pause", new { key = old })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await laptop.PutAsJsonAsync("/api/timer", new { key = old, description = "Overschreven" })).StatusCode);
        var still = await TimerAsync(laptop);
        Assert.Equal(current, Key(still));
        Assert.Equal("Nieuw", still.GetProperty("description").GetString());
        Assert.Equal(JsonValueKind.Null, still.GetProperty("pausedAt").ValueKind);

        // Weggooien met het juiste kenmerk boekt niets.
        Assert.Equal(HttpStatusCode.OK, (await phone.DeleteAsync($"/api/timer?key={current}")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await TimerAsync(laptop)).ValueKind);
        Assert.Single((await laptop.GetFromJsonAsync<List<JsonElement>>($"/api/time?projectId={projectId}"))!);
    }

    [Fact]
    public async Task Nobody_sees_or_stops_the_timer_of_a_colleague()
    {
        factory.At(14, 9);
        var admin = await RegisterAsync(factory, "timer-team@example.com");
        await CreateUserAsync(admin, "timer-team-collega@example.com");
        var colleague = await LoginAsync(factory, "timer-team-collega@example.com");
        var projectId = await ProjectAsync(admin);

        // Een medewerker gebruikt de timer net als een beheerder.
        var theirs = Key(await StartAsync(colleague, projectId, "Van de collega"));
        Assert.Equal(JsonValueKind.Null, (await TimerAsync(admin)).ValueKind);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/timer/stop", new { key = theirs })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/timer/pause", new { key = theirs })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/timer/switch", new { key = theirs, projectId })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/timer?key={theirs}")).StatusCode);
        Assert.Equal(theirs, Key(await TimerAsync(colleague)));

        // Ieder heeft zijn eigen timer, tegelijk.
        var mine = Key(await StartAsync(admin, projectId, "Van de beheerder"));
        factory.Later(TimeSpan.FromMinutes(15));
        Assert.Equal(theirs, Key(await TimerAsync(colleague)));
        var colleagueEntry = (await StopAsync(colleague, theirs)).GetProperty("entry");
        Assert.Equal(15, colleagueEntry.GetProperty("minutes").GetInt32());
        Assert.Equal(await MyIdAsync(colleague), colleagueEntry.GetProperty("userId").GetInt32());
        Assert.Equal(mine, Key(await TimerAsync(admin)));
    }

    [Fact]
    public async Task Projects_of_another_workspace_cannot_be_used()
    {
        factory.At(14, 9);
        var a = await RegisterAsync(factory, "timer-ws-eigen@example.com");
        var b = await RegisterAsync(factory, "timer-ws-ander@example.com");
        var own = await ProjectAsync(a);
        var foreign = await ProjectAsync(b, "Van B");

        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/timer", new { projectId = foreign })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/timer/import",
            new { key = "oud-1", projectId = foreign, startedAt = factory.Clock.Value.AddMinutes(-5) })).StatusCode);
        var key = Key(await StartAsync(a, own));
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/timer/switch", new { key, projectId = foreign })).StatusCode);
        Assert.Equal(own, (await TimerAsync(a)).GetProperty("projectId").GetInt32());
        Assert.Equal(JsonValueKind.Null, (await TimerAsync(b)).ValueKind);
    }

    [Fact]
    public async Task A_read_only_workspace_keeps_its_timer_but_cannot_start_or_book_it()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "timer-alleenlezen@example.com");
        var projectId = await ProjectAsync(c);
        var other = await ProjectAsync(c, "Ander");
        var key = Key(await StartAsync(c, projectId));
        await ExpireTrialAsync(factory, await WorkspaceIdAsync(c));
        factory.Later(TimeSpan.FromMinutes(30));

        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/timer/stop", new { key })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/timer/switch", new { key, projectId = other })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/timer/pause", new { key })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PutAsJsonAsync("/api/timer", new { key, description = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/time", new { projectId, date = "2026-10-14", minutes = 30 })).StatusCode);
        Assert.Equal(key, Key(await TimerAsync(c)));
        Assert.Empty((await c.GetFromJsonAsync<List<JsonElement>>($"/api/time?projectId={projectId}"))!);

        // Weggooien mag wel, er wordt dan niets geboekt. Een nieuwe timer starten niet.
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/timer?key={key}")).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await TimerAsync(c)).ValueKind);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/timer", new { projectId })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/timer/import",
            new { key = "oud-alleenlezen", projectId, startedAt = factory.Clock.Value.AddMinutes(-5) })).StatusCode);
        Assert.Equal(0, factory.RunningTimers("timer-alleenlezen@example.com"));
    }

    [Fact]
    public async Task A_timer_from_the_browser_is_moved_to_the_server_once()
    {
        factory.At(14, 10);
        var c = await RegisterAsync(factory, "timer-overzetten@example.com");
        var projectId = await ProjectAsync(c);
        var startedAt = factory.Clock.Value.AddMinutes(-40);
        object Legacy(string key) => new { key, projectId, description = "Uit de browser", billable = true, startedAt };

        var imported = (await (await c.PostAsJsonAsync("/api/timer/import", Legacy("browser-123"))).JsonAsync()).GetProperty("timer");
        Assert.Equal("browser-123", Key(imported));
        Assert.Equal(40 * 60, imported.GetProperty("elapsedSeconds").GetInt32());
        // Een tweede tabblad dat hetzelfde overzet, verandert niets.
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/api/timer/import", Legacy("browser-123"))).StatusCode);
        Assert.Equal(1, factory.RunningTimers("timer-overzetten@example.com"));
        // Een andere browser-timer terwijl er al een loopt: die op de server blijft.
        var other = await c.PostAsJsonAsync("/api/timer/import", Legacy("browser-456"));
        Assert.Equal(HttpStatusCode.Conflict, other.StatusCode);
        Assert.Equal("browser-123", Key((await other.JsonAsync()).GetProperty("timer")));

        var entry = (await StopAsync(c, "browser-123")).GetProperty("entry");
        Assert.Equal(40, entry.GetProperty("minutes").GetInt32());
        Assert.Equal("browser-123", entry.GetProperty("clientId").GetString());
        // Al geboekt: nog een keer overzetten start niets.
        var again = await (await c.PostAsJsonAsync("/api/timer/import", Legacy("browser-123"))).JsonAsync();
        Assert.Equal(JsonValueKind.Null, again.GetProperty("timer").ValueKind);
        Assert.Single((await c.GetFromJsonAsync<List<JsonElement>>($"/api/time?projectId={projectId}"))!);
    }

    [Fact]
    public async Task A_booking_from_an_old_tab_counts_once_after_moving_the_timer()
    {
        factory.At(14, 10);
        var c = await RegisterAsync(factory, "timer-oudtabblad@example.com");
        var projectId = await ProjectAsync(c);
        await c.PostAsJsonAsync("/api/timer/import", new { key = "tab-1", projectId, description = "X", billable = true, startedAt = factory.Clock.Value.AddMinutes(-30) });
        // Een tabblad met de vorige versie van Potel stopt dezelfde timer nog op de oude manier.
        (await c.PostAsJsonAsync("/api/time", new { projectId, date = "2026-10-14", minutes = 30, description = "X", billable = true, clientId = "tab-1" })).EnsureSuccessStatusCode();
        factory.Later(TimeSpan.FromMinutes(5));

        var stopped = await StopAsync(c, "tab-1");
        Assert.Equal(30, stopped.GetProperty("entry").GetProperty("minutes").GetInt32());
        Assert.Single((await c.GetFromJsonAsync<List<JsonElement>>($"/api/time?projectId={projectId}"))!);
        Assert.Equal(JsonValueKind.Null, (await TimerAsync(c)).ValueKind);
    }

    [Fact]
    public async Task A_project_with_a_running_timer_stays_until_the_timer_stops_and_a_removed_user_takes_it_along()
    {
        factory.At(14, 9);
        var admin = await RegisterAsync(factory, "timer-weg@example.com");
        var colleagueId = await CreateUserAsync(admin, "timer-weg-collega@example.com");
        var colleague = await LoginAsync(factory, "timer-weg-collega@example.com");
        var projectId = await ProjectAsync(admin);
        await StartAsync(colleague, projectId);

        var refused = await admin.DeleteAsync($"/api/projects/{projectId}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("timer", await refused.ErrorAsync());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{colleagueId}")).StatusCode);
        Assert.Equal(0, factory.RunningTimers("timer-weg@example.com"));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/projects/{projectId}")).StatusCode);
    }

    [Fact]
    public async Task Removing_demo_data_or_the_workspace_handles_a_running_timer()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "timer-demo@example.com");
        (await c.PostAsync("/api/workspace/demo-data", null)).EnsureSuccessStatusCode();
        var demoProject = (await c.GetFromJsonAsync<List<JsonElement>>("/api/projects"))!.First().GetProperty("id").GetInt32();
        var key = Key(await StartAsync(c, demoProject));

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/workspace/demo-data")).StatusCode);
        Assert.Equal(key, Key(await TimerAsync(c)));
        Assert.Contains((await c.GetFromJsonAsync<List<JsonElement>>("/api/projects"))!, p => p.GetProperty("id").GetInt32() == demoProject);

        var delete = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) };
        Assert.Equal(HttpStatusCode.NoContent, (await c.SendAsync(delete)).StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<AppDb>().RunningTimers.IgnoreQueryFilters().Any(t => t.Key == key));
    }
}

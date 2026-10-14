using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Focusmodus op de timer: blokken werk met pauzes ertussen. De timer boekt gewoon de gewerkte tijd; pauzes tellen niet mee.
public class FocusTests(TimerFactory factory) : IClassFixture<TimerFactory>
{
    static async Task<int> ProjectAsync(HttpClient c, string name = "App")
    {
        var customer = await CreateCustomerAsync(c);
        var res = await c.PostAsJsonAsync("/api/projects", new { name, customerId = customer, status = "actief", billing = "uur", hourlyRate = 95, color = "#4ea5ff" });
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    static async Task<JsonElement> FocusAsync(HttpClient c, int projectId, int minutes = 25, string description = "Diep werk")
    {
        var res = await c.PostAsJsonAsync("/api/timer/focus", new { minutes, projectId, description });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.JsonAsync()).GetProperty("timer");
    }

    static async Task<JsonElement> TimerAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonElement>("/api/timer")).GetProperty("timer");
    static JsonElement Focus(JsonElement timer) => timer.GetProperty("focus");
    static string Phase(JsonElement timer) => Focus(timer).GetProperty("phase").GetString()!;
    static int Remaining(JsonElement timer) => Focus(timer).GetProperty("remainingSeconds").GetInt32();
    static int Blocks(JsonElement timer) => Focus(timer).GetProperty("blocks").GetInt32();
    static string Key(JsonElement timer) => timer.GetProperty("key").GetString()!;

    static async Task<JsonElement> PostOkAsync(HttpClient c, string path, object body)
    {
        var res = await c.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.JsonAsync();
    }

    static async Task<JsonElement> StopAsync(HttpClient c, string key) =>
        (await PostOkAsync(c, "/api/timer/stop", new { key })).GetProperty("entry");

    static async Task<List<(string Date, int Blocks)>> DaysAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<List<JsonElement>>("/api/timer/focus/days?from=2026-10-12&to=2026-10-18"))!
            .Select(d => (d.GetProperty("date").GetString()!, d.GetProperty("blocks").GetInt32())).ToList();

    [Fact]
    public async Task A_block_ends_after_25_minutes_and_the_break_is_not_booked()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "focus-blok@example.com");
        var projectId = await ProjectAsync(c);
        Assert.Equal(25, (await c.GetFromJsonAsync<JsonElement>("/api/timer")).GetProperty("focusMinutes").GetInt32());

        var timer = await FocusAsync(c, projectId);
        Assert.Equal("focus", Phase(timer));
        Assert.Equal(1500, Remaining(timer));
        Assert.Equal("Diep werk", timer.GetProperty("description").GetString());

        factory.Later(TimeSpan.FromMinutes(10));
        Assert.Equal(900, Remaining(await TimerAsync(c)));

        // Om 9:25 was het blok vol: de pauze van 5 minuten loopt en de timer staat stil.
        factory.Later(TimeSpan.FromMinutes(17));
        timer = await TimerAsync(c);
        Assert.Equal("break", Phase(timer));
        Assert.Equal(1, Blocks(timer));
        Assert.Equal(5, Focus(timer).GetProperty("breakMinutes").GetInt32());
        Assert.Equal(180, Remaining(timer));
        Assert.Equal(1500, timer.GetProperty("elapsedSeconds").GetInt32());
        Assert.Equal(factory.Clock.Value.AddMinutes(-2), timer.GetProperty("pausedAt").GetDateTime().ToUniversalTime());

        // Uren later is de pauze allang om, maar geboekt wordt alleen het blok, zonder te vragen naar de eindtijd.
        factory.Later(TimeSpan.FromHours(3));
        timer = await TimerAsync(c);
        Assert.Equal("ready", Phase(timer));
        Assert.False(timer.GetProperty("needsConfirmation").GetBoolean());
        var entry = await StopAsync(c, Key(timer));
        Assert.Equal(25, entry.GetProperty("minutes").GetInt32());
        Assert.Equal(1, entry.GetProperty("focusBlocks").GetInt32());
    }

    [Fact]
    public async Task Continuing_after_the_break_starts_the_next_block_and_only_work_is_booked()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "focus-verder@example.com");
        var key = Key(await FocusAsync(c, await ProjectAsync(c)));
        factory.Later(TimeSpan.FromMinutes(27));

        var timer = (await PostOkAsync(c, "/api/timer/resume", new { key })).GetProperty("timer");
        Assert.Equal("focus", Phase(timer));
        Assert.Equal(1500, Remaining(timer));
        Assert.Equal(1500, Focus(timer).GetProperty("blockStart").GetInt32());
        Assert.Equal(JsonValueKind.Null, timer.GetProperty("pausedAt").ValueKind);

        factory.Later(TimeSpan.FromMinutes(10));
        var entry = await StopAsync(c, key);
        Assert.Equal(35, entry.GetProperty("minutes").GetInt32());
        Assert.Equal(1, entry.GetProperty("focusBlocks").GetInt32());
    }

    [Fact]
    public async Task After_four_blocks_comes_a_long_break()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "focus-lang@example.com");
        var key = Key(await FocusAsync(c, await ProjectAsync(c), minutes: 15));

        foreach (var (block, pause) in new[] { (1, 5), (2, 5), (3, 5), (4, 15), (5, 5) })
        {
            factory.Later(TimeSpan.FromMinutes(15));
            var timer = await TimerAsync(c);
            Assert.Equal("break", Phase(timer));
            Assert.Equal(block, Blocks(timer));
            Assert.Equal(pause, Focus(timer).GetProperty("breakMinutes").GetInt32());
            Assert.Equal(block % 4, Focus(timer).GetProperty("round").GetInt32());
            factory.Later(TimeSpan.FromMinutes(pause));
            await PostOkAsync(c, "/api/timer/resume", new { key });
        }

        // Vijf blokken van 15 minuten gewerkt, de pauzes niet, en het blok dat net begon is nog niets.
        var entry = await StopAsync(c, key);
        Assert.Equal(75, entry.GetProperty("minutes").GetInt32());
        Assert.Equal(5, entry.GetProperty("focusBlocks").GetInt32());
    }

    [Fact]
    public async Task A_block_is_15_to_90_minutes_and_the_choice_follows_you_to_every_device()
    {
        factory.At(14, 9);
        var laptop = await RegisterAsync(factory, "focus-lengte@example.com");
        await CreateUserAsync(laptop, "focus-lengte-collega@example.com");
        var projectId = await ProjectAsync(laptop);

        foreach (var minutes in new[] { 0, 10, 14, 91, 120 })
        {
            var res = await laptop.PostAsJsonAsync("/api/timer/focus", new { minutes, projectId });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Equal(Potel.Api.Endpoints.TimerEndpoints.FocusLength, await res.ErrorAsync());
        }
        Assert.Equal(0, factory.RunningTimers("focus-lengte@example.com"));

        var timer = await FocusAsync(laptop, projectId, minutes: 50);
        Assert.Equal(3000, Remaining(timer));
        var phone = await LoginAsync(factory, "focus-lengte@example.com");
        Assert.Equal(50, (await phone.GetFromJsonAsync<JsonElement>("/api/timer")).GetProperty("focusMinutes").GetInt32());
        var colleague = await LoginAsync(factory, "focus-lengte-collega@example.com");
        Assert.Equal(25, (await colleague.GetFromJsonAsync<JsonElement>("/api/timer")).GetProperty("focusMinutes").GetInt32());

        // De grenzen zelf mogen wel.
        await PostOkAsync(phone, "/api/timer/focus", new { key = Key(timer), minutes = 90 });
        await PostOkAsync(phone, "/api/timer/focus", new { key = Key(timer), minutes = 15 });
        Assert.Equal(15, (await laptop.GetFromJsonAsync<JsonElement>("/api/timer")).GetProperty("focusMinutes").GetInt32());
    }

    [Fact]
    public async Task Pausing_yourself_during_a_block_makes_the_block_last_longer()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "focus-pauze@example.com");
        var key = Key(await FocusAsync(c, await ProjectAsync(c)));
        factory.Later(TimeSpan.FromMinutes(10));
        await PostOkAsync(c, "/api/timer/pause", new { key });
        factory.Later(TimeSpan.FromMinutes(20));

        var timer = await TimerAsync(c);
        Assert.Equal("focus", Phase(timer));
        Assert.Equal(900, Remaining(timer));

        await PostOkAsync(c, "/api/timer/resume", new { key });
        factory.Later(TimeSpan.FromMinutes(14));
        Assert.Equal(60, Remaining(await TimerAsync(c)));
        factory.Later(TimeSpan.FromMinutes(2));
        timer = await TimerAsync(c);
        Assert.Equal("break", Phase(timer));
        Assert.Equal(240, Remaining(timer));

        Assert.Equal(25, (await StopAsync(c, key)).GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task Focus_can_be_switched_on_for_a_running_timer_and_off_again()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "focus-aanuit@example.com");
        var projectId = await ProjectAsync(c);
        var started = await c.PostAsJsonAsync("/api/timer", new { projectId, description = "Gewoon" });
        var key = Key((await started.JsonAsync()).GetProperty("timer"));
        factory.Later(TimeSpan.FromMinutes(40));

        // Zonder kenmerk is het een nieuwe timer, en die loopt er al; met een oud kenmerk is het een verouderd scherm.
        var refused = await c.PostAsJsonAsync("/api/timer/focus", new { minutes = 25, projectId });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(key, Key((await refused.JsonAsync()).GetProperty("timer")));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/timer/focus", new { key = "timer-oud", minutes = 25 })).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await TimerAsync(c)).GetProperty("focus").ValueKind);

        var timer = (await PostOkAsync(c, "/api/timer/focus", new { key, minutes = 25 })).GetProperty("timer");
        Assert.Equal(key, Key(timer));
        Assert.Equal(2400, timer.GetProperty("elapsedSeconds").GetInt32());
        Assert.Equal(1500, Remaining(timer));
        factory.Later(TimeSpan.FromMinutes(26));
        Assert.Equal("break", Phase(await TimerAsync(c)));

        // Uit tijdens de pauze: een gewone timer die stilstaat tot je verder gaat. Het afgemaakte blok blijft tellen.
        var res = await c.DeleteAsync($"/api/timer/focus?key={key}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        timer = (await res.JsonAsync()).GetProperty("timer");
        Assert.Equal(JsonValueKind.Null, timer.GetProperty("focus").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, timer.GetProperty("pausedAt").ValueKind);
        await PostOkAsync(c, "/api/timer/resume", new { key });
        factory.Later(TimeSpan.FromMinutes(5));

        var entry = await StopAsync(c, key);
        Assert.Equal(40 + 25 + 5, entry.GetProperty("minutes").GetInt32());
        Assert.Equal(1, entry.GetProperty("focusBlocks").GetInt32());

        // Er loopt niets meer: een scherm dat dat nog niet wist, zet geen focus aan.
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/timer/focus", new { key, minutes = 25 })).StatusCode);
        Assert.Equal(0, factory.RunningTimers("focus-aanuit@example.com"));
    }

    [Fact]
    public async Task Two_devices_starting_focus_at_once_get_one_timer()
    {
        factory.At(14, 9);
        var laptop = await RegisterAsync(factory, "focus-tegelijk@example.com");
        var phone = await LoginAsync(factory, "focus-tegelijk@example.com");
        var projectId = await ProjectAsync(laptop);

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => i % 3 == 0
            ? (i % 2 == 0 ? laptop : phone).PostAsJsonAsync("/api/timer", new { projectId })
            : (i % 2 == 0 ? laptop : phone).PostAsJsonAsync("/api/timer/focus", new { minutes = 25, projectId })));

        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, factory.RunningTimers("focus-tegelijk@example.com"));
    }

    [Fact]
    public async Task Switching_project_keeps_the_block_and_the_break_going()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "focus-wissel@example.com");
        var first = await ProjectAsync(c, "Eerste");
        var second = await ProjectAsync(c, "Tweede");
        var key = Key(await FocusAsync(c, first));
        factory.Later(TimeSpan.FromMinutes(10));

        var switched = await PostOkAsync(c, "/api/timer/switch", new { key, projectId = second, description = "Verder" });
        Assert.Equal(10, switched.GetProperty("entry").GetProperty("minutes").GetInt32());
        Assert.Equal(0, switched.GetProperty("entry").GetProperty("focusBlocks").GetInt32());
        var timer = switched.GetProperty("timer");
        Assert.Equal("focus", Phase(timer));
        Assert.Equal(900, Remaining(timer));

        factory.Later(TimeSpan.FromMinutes(16));
        timer = await TimerAsync(c);
        Assert.Equal("break", Phase(timer));
        Assert.Equal(1, Blocks(timer));
        var breakStarted = Focus(timer).GetProperty("breakStartedAt").GetDateTime();

        // In de pauze naar een ander project: dat blok komt op de boeking, en de pauze loopt gewoon door.
        switched = await PostOkAsync(c, "/api/timer/switch", new { key = Key(timer), projectId = first });
        Assert.Equal(15, switched.GetProperty("entry").GetProperty("minutes").GetInt32());
        Assert.Equal(1, switched.GetProperty("entry").GetProperty("focusBlocks").GetInt32());
        timer = switched.GetProperty("timer");
        Assert.Equal("break", Phase(timer));
        Assert.Equal(breakStarted, Focus(timer).GetProperty("breakStartedAt").GetDateTime());
        Assert.Equal(0, timer.GetProperty("elapsedSeconds").GetInt32());

        timer = (await PostOkAsync(c, "/api/timer/resume", new { key = Key(timer) })).GetProperty("timer");
        Assert.Equal(1500, Remaining(timer));
        factory.Later(TimeSpan.FromMinutes(5));
        Assert.Equal(5, (await StopAsync(c, Key(timer))).GetProperty("minutes").GetInt32());
    }

    [Fact]
    public async Task Focus_blocks_per_day_are_your_own()
    {
        factory.At(13, 9);
        var c = await RegisterAsync(factory, "focus-dagen@example.com");
        await CreateUserAsync(c, "focus-dagen-collega@example.com");
        var colleague = await LoginAsync(factory, "focus-dagen-collega@example.com");
        var other = await RegisterAsync(factory, "focus-dagen-ander@example.com");
        var projectId = await ProjectAsync(c);

        var key = Key(await FocusAsync(c, projectId, minutes: 15));
        factory.Later(TimeSpan.FromMinutes(20));
        await StopAsync(c, key);

        // Vandaag twee blokken, en de timer loopt nog: die tellen al mee.
        factory.At(14, 9);
        key = Key(await FocusAsync(c, projectId, minutes: 15));
        factory.Later(TimeSpan.FromMinutes(20));
        await PostOkAsync(c, "/api/timer/resume", new { key });
        factory.Later(TimeSpan.FromMinutes(15));
        // Wat de browser bij een gewone boeking meestuurt, telt niet als focusblok.
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/time", new { projectId, date = "2026-10-14", minutes = 30, focusBlocks = 9 })).StatusCode);

        Assert.Equal(new[] { ("2026-10-13", 1), ("2026-10-14", 2) }, await DaysAsync(c));
        Assert.Empty(await DaysAsync(colleague));
        Assert.Empty(await DaysAsync(other));

        // Een collega kan je focusmodus niet uitzetten of je volgende blok starten.
        Assert.Equal(HttpStatusCode.Conflict, (await colleague.DeleteAsync($"/api/timer/focus?key={key}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await colleague.PostAsJsonAsync("/api/timer/resume", new { key })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsJsonAsync("/api/timer/focus", new { minutes = 25, projectId })).StatusCode);
        Assert.Equal("break", Phase(await TimerAsync(c)));

        Assert.Equal(2, (await StopAsync(c, key)).GetProperty("focusBlocks").GetInt32());
        Assert.Equal(new[] { ("2026-10-13", 1), ("2026-10-14", 2) }, await DaysAsync(c));
    }

    [Fact]
    public async Task A_read_only_workspace_shows_the_focus_state_but_cannot_change_it()
    {
        factory.At(14, 9);
        var c = await RegisterAsync(factory, "focus-alleenlezen@example.com");
        var projectId = await ProjectAsync(c);
        var key = Key(await FocusAsync(c, projectId));
        await ExpireTrialAsync(factory, await WorkspaceIdAsync(c));
        factory.Later(TimeSpan.FromMinutes(28));

        // Ophalen rekent de pauze uit, maar schrijft niets.
        Assert.Equal("break", Phase(await TimerAsync(c)));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDb>();
            var stored = db.RunningTimers.IgnoreQueryFilters().AsNoTracking().Single(t => t.Key == key);
            Assert.Null(stored.FocusBreakStartedAt);
            Assert.Null(stored.PausedAt);
        }
        Assert.Equal(new[] { ("2026-10-14", 1) }, await DaysAsync(c));

        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/timer/focus", new { key, minutes = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.DeleteAsync($"/api/timer/focus?key={key}")).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/timer/resume", new { key })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/timer/stop", new { key })).StatusCode);

        // Weggooien mag, net als bij een gewone timer; daarna start er geen nieuwe.
        Assert.Equal(HttpStatusCode.OK, (await c.DeleteAsync($"/api/timer?key={key}")).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PostAsJsonAsync("/api/timer/focus", new { minutes = 25, projectId })).StatusCode);
        Assert.Equal(0, factory.RunningTimers("focus-alleenlezen@example.com"));
    }
}

// De migratie voor de focusmodus voegt alleen kolommen toe: bestaande uren, gebruikers en een lopende timer blijven zoals ze waren.
public class FocusMigrationTests : IDisposable
{
    const string Before = "20261014081958_TimerOpDeServer";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-focus-{Guid.NewGuid():N}.db");

    AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant());

    object? Scalar(string sql)
    {
        using var con = new SqliteConnection($"Data Source={path}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        var value = cmd.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    [Fact]
    public void Existing_hours_users_and_a_running_timer_survive()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 'Oud bedrijf', 'team');
                INSERT INTO Users (Id, WorkspaceId, Active, CreatedAt, Email, IsPlatformAdmin, Name, PasswordHash, Role, SecurityStamp)
                    VALUES (1, 1, 1, '2026-01-01 00:00:00', 'jan@example.com', 0, 'Jan', 'hash', 'beheerder', 'stempel');
                INSERT INTO Customers (Id, WorkspaceId, IsDemo, CreatedAt, Name, Company, Country, RemindersOff, Language) VALUES (1, 1, 0, '2026-01-01 00:00:00', 'Jan', 'Klant B.V.', 'Nederland', 0, 'nl');
                INSERT INTO Projects (Id, WorkspaceId, Billing, Color, CreatedAt, CustomerId, Description, FixedPrice, HourlyRate, IsDemo, Name, RepoUrl, Status)
                    VALUES (1, 1, 'uur', '#4ea5ff', '2026-01-01 00:00:00', 1, '', 0, 95, 0, 'Webshop', '', 'actief');
                INSERT INTO TimeEntries (Id, WorkspaceId, Billable, ClientId, CreatedAt, Date, Description, IsDemo, Minutes, ProjectId, UserId)
                    VALUES (1, 1, 1, 'timer-oud', '2026-10-13 10:00:00', '2026-10-13 00:00:00', 'Inloggen bouwen', 0, 95, 1, 1);
                INSERT INTO RunningTimers (Id, WorkspaceId, Billable, Description, Key, PausedSeconds, ProjectId, StartedAt, UpdatedAt, UserId)
                    VALUES (1, 1, 1, 'Loopt nog', 'timer-loopt', 60, 1, '2026-10-14 07:00:00', '2026-10-14 07:00:00', 1);
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = Db()) db.GetService<IMigrator>().Migrate();

        Assert.Equal(95L, Scalar("SELECT Minutes FROM TimeEntries WHERE Id = 1"));
        Assert.Equal("Inloggen bouwen", Scalar("SELECT Description FROM TimeEntries WHERE Id = 1"));
        Assert.Equal(0L, Scalar("SELECT FocusBlocks FROM TimeEntries WHERE Id = 1"));
        Assert.Equal("timer-loopt", Scalar("SELECT Key FROM RunningTimers WHERE Id = 1"));
        Assert.Equal(60L, Scalar("SELECT PausedSeconds FROM RunningTimers WHERE Id = 1"));
        Assert.Null(Scalar("SELECT FocusMinutes FROM RunningTimers WHERE Id = 1"));
        Assert.Equal(0L, Scalar("SELECT FocusBlocks FROM RunningTimers WHERE Id = 1"));
        Assert.Equal("jan@example.com", Scalar("SELECT Email FROM Users WHERE Id = 1"));
        Assert.Null(Scalar("SELECT FocusMinutes FROM Users WHERE Id = 1"));
    }

    public void Dispose()
    {
        TestDatabase.Delete(path);
    }
}

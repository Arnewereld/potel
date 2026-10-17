using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Endpoints;
using Potel.Api.Invoicing;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

public class CalendarFactory : PortalFactory
{
    public MovableClock Clock { get; } = new(new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:CalendarFeedPerMinute", "10000");
        builder.ConfigureTestServices(s => s.AddSingleton<BusinessClock>(Clock));
    }
}

// Een strenge limiet op de agendalink, om de begrenzing zelf te testen.
public class StrictCalendarFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:CalendarFeedPerMinute", "3");
    }
}

// Hulpjes om een agenda te lezen zoals een agenda-app dat doet: regels samenvoegen en per afspraak de velden.
static class Ics
{
    public static string Unfold(string body) => body.Replace("\r\n ", "");

    public static List<Dictionary<string, string>> Events(string body)
    {
        var events = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        foreach (var line in Unfold(body).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line == "BEGIN:VEVENT") current = [];
            else if (line == "END:VEVENT") { events.Add(current!); current = null; }
            else if (current is not null)
            {
                var colon = line.IndexOf(':');
                current[line[..colon]] = line[(colon + 1)..];
            }
        }
        return events;
    }

    public static Dictionary<string, string> Titled(string body, string summary) => Assert.Single(Events(body), e => e["SUMMARY"] == summary);
}

// De bouwstenen van een agenda volgens RFC 5545, zonder server.
public class IcsWriterTests
{
    [Fact]
    public void Text_is_escaped()
    {
        Assert.Equal(@"Jansen\, Zn\; B.V. \\ kantoor\nTweede regel\nDerde", IcsWriter.Escape("Jansen, Zn; B.V. \\ kantoor\r\nTweede regel\rDerde\u0007"));
    }

    [Fact]
    public void Long_lines_fold_at_75_octets_without_cutting_a_character()
    {
        var text = "SUMMARY:" + string.Concat(Enumerable.Repeat("Overleg café 🎉 über de planning, ", 12));
        var folded = IcsWriter.Fold(text);
        var lines = folded.Split("\r\n");
        Assert.True(lines.Length > 5);
        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        foreach (var line in lines)
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            Assert.True(bytes.Length <= 75, $"{bytes.Length} bytes: {line}");
            // Elke regel is op zich geldige UTF-8: er is geen teken in tweeën geknipt.
            Assert.Equal(line, strict.GetString(bytes));
        }
        Assert.All(lines.Skip(1), l => Assert.StartsWith(" ", l));
        Assert.Equal(text, Ics.Unfold(folded));
        Assert.Equal("KORT:ja", IcsWriter.Fold("KORT:ja"));
    }

    [Fact]
    public void Dutch_times_become_utc_also_around_daylight_saving()
    {
        Assert.Equal(new DateTime(2026, 7, 1, 8, 0, 0), IcsWriter.ToUtc(new DateTime(2026, 7, 1, 10, 0, 0)));
        Assert.Equal(new DateTime(2026, 1, 15, 9, 0, 0), IcsWriter.ToUtc(new DateTime(2026, 1, 15, 10, 0, 0)));
        // 02:30 bestaat niet op de dag dat de zomertijd begint; dat wordt 03:30 zomertijd.
        Assert.Equal(new DateTime(2026, 3, 29, 1, 30, 0), IcsWriter.ToUtc(new DateTime(2026, 3, 29, 2, 30, 0)));
    }

    [Fact]
    public void A_whole_day_is_a_date_with_an_exclusive_end()
    {
        var body = IcsWriter.Write("Potel", "Test", [new IcsEvent("x-1@potel", "Deadline", new DateTime(2026, 12, 31), null, true)], new DateTime(2026, 10, 12, 8, 0, 0));
        var e = Assert.Single(Ics.Events(body));
        Assert.Equal("20261231", e["DTSTART;VALUE=DATE"]);
        Assert.Equal("20270101", e["DTEND;VALUE=DATE"]);
        Assert.Equal("20261012T080000Z", e["DTSTAMP"]);
        Assert.Equal("TRANSPARENT", e["TRANSP"]);
    }
}

// Je planning in je eigen agenda via een geheime link per gebruiker: een geldige agenda, alleen de hash in de database, hetzelfde 404
// bij elke fout, zonder cookie, per gebruiker en per werkruimte, geld alleen voor een beheerder, en ook als je werkruimte alleen-lezen is.
// De klok staat op maandag 12 oktober 2026.
public class CalendarFeedTests(CalendarFactory factory) : IClassFixture<CalendarFactory>
{
    record Setup(HttpClient Admin, int Ws, int AdminId, int Customer);

    async Task<Setup> ReadyAsync(string prefix)
    {
        factory.Clock.SetDay(2026, 10, 12);
        var c = await InvoiceKit.ReadyAsync(factory, prefix);
        var res = await c.PostAsJsonAsync("/api/customers", new { name = "Sanne", company = "Bakkerij, Jansen & Zn; B.V.", address = "Dorpsstraat 1", city = "1234 AB Utrecht" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return new Setup(c, await TestApi.WorkspaceIdAsync(c), await TestApi.MyIdAsync(c), (await res.JsonAsync()).GetProperty("id").GetInt32());
    }

    // Zonder cookie, zoals Google, Apple of Outlook de link ophaalt.
    HttpClient Calendar() => factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    static async Task<string> LinkAsync(HttpClient c)
    {
        var res = await c.PostAsync("/api/calendar-feed/link", null);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var json = await res.JsonAsync();
        var path = json.GetProperty("path").GetString()!;
        Assert.Matches(@"^/api/public/agenda/[A-Za-z0-9_-]{43}\.ics$", path);
        // In ontwikkeling maakt de server zelf het volledige adres.
        Assert.EndsWith(path, json.GetProperty("url").GetString());
        return path;
    }

    static string TokenOf(string path) => path["/api/public/agenda/".Length..^".ics".Length];

    async Task<string> FeedAsync(string path)
    {
        var res = await Calendar().GetAsync(path);
        Assert.True(res.StatusCode == HttpStatusCode.OK, $"{res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadAsStringAsync();
    }

    static async Task<int> AppointmentAsync(HttpClient c, string title, string start, string end, int? userId = null, string kind = "afspraak", int? customerId = null,
        string? location = null, string? notes = null, bool done = false)
    {
        var res = await c.PostAsJsonAsync("/api/appointments", new { title, start, end, kind, customerId, location, notes, done, userId });
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    static Task<HttpResponseMessage> OptionsAsync(HttpClient c, bool planning = true, bool deadlines = true, bool invoices = true, bool quotes = true, bool vat = true,
        bool showAmounts = false) =>
        c.PutAsJsonAsync("/api/calendar-feed", new { planning, deadlines, invoices, quotes, vat, showAmounts });

    // Een verstuurde factuur, een verstuurde offerte en een project met een deadline, voor de geldzaken in de agenda.
    static async Task MoneyAsync(Setup s)
    {
        await InvoiceAsync(s.Admin, s.Customer, "verzonden", 2, 100, issueDate: "2026-10-12");
        var quote = await s.Admin.PostAsJsonAsync("/api/quotes", new
        {
            customerId = s.Customer, issueDate = "2026-10-12", validUntil = "2026-11-11", vatRegime = "normaal", subject = "Nieuwe webshop",
            lines = new[] { new { description = "Webshop bouwen", quantity = 10, unit = "uur", unitPrice = 100, vatRate = 21 } },
        });
        Assert.True(quote.StatusCode == HttpStatusCode.Created, await quote.Content.ReadAsStringAsync());
        var id = (await quote.JsonAsync()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync($"/api/quotes/{id}/send", null)).StatusCode);
        var project = await s.Admin.PostAsJsonAsync("/api/projects", new { name = "Webshop", customerId = s.Customer, status = "actief", billing = "uur", hourlyRate = 95, color = "#000000", deadline = "2026-11-20" });
        Assert.Equal(HttpStatusCode.Created, project.StatusCode);
    }

    [Fact]
    public async Task The_feed_is_valid_icalendar()
    {
        var s = await ReadyAsync("ics-geldig");
        var title = "Kennismaking met de nieuwe klant over de planning van de webshop, café en 🎉 alles";
        await AppointmentAsync(s.Admin, title, "2026-10-14T10:00", "2026-10-14T11:30", s.AdminId, customerId: s.Customer, location: "Utrecht, kantoor; 2e verdieping",
            notes: "Geheime notitie over de prijs");
        await AppointmentAsync(s.Admin, "Winteroverleg", "2026-12-01T09:00", "2026-12-01T09:45");
        await AppointmentAsync(s.Admin, "Afgevinkte taak", "2026-10-13T09:00", "2026-10-13T10:00", kind: "taak", done: true);
        await AppointmentAsync(s.Admin, "Gedane afspraak", "2026-10-09T09:00", "2026-10-09T10:00", done: true);
        await AppointmentAsync(s.Admin, "Lang geleden", "2026-03-01T09:00", "2026-03-01T10:00");
        await MoneyAsync(s);
        var path = await LinkAsync(s.Admin);

        var res = await Calendar().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/calendar", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", res.Content.Headers.ContentType.CharSet);
        Assert.Equal("no-store", res.Headers.CacheControl!.ToString());
        Assert.Contains("noindex", res.Headers.GetValues("X-Robots-Tag").Single());
        var body = await res.Content.ReadAsStringAsync();

        // CRLF overal, geen regel langer dan 75 bytes, begin en einde zoals het hoort.
        Assert.DoesNotContain("\n", body.Replace("\r\n", ""));
        Assert.EndsWith("END:VCALENDAR\r\n", body);
        var lines = body.Split("\r\n")[..^1];
        Assert.All(lines, l => Assert.True(Encoding.UTF8.GetByteCount(l) <= 75, l));
        Assert.Equal(new[] { "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Potel//Agenda//NL" }, lines[..3]);
        Assert.Contains("X-WR-CALNAME:Potel\\, Mijn Bedrijf", lines);
        Assert.Contains(lines, l => l.StartsWith(' '));

        var events = Ics.Events(body);
        Assert.All(events, e =>
        {
            Assert.Matches(@"^[a-z]+-[0-9a-z-]+\.ws\d+@potel$", e["UID"]);
            Assert.Matches(@"^\d{8}T\d{6}Z$", e["DTSTAMP"]);
            Assert.True(e.ContainsKey("DTSTART") || e.ContainsKey("DTSTART;VALUE=DATE"));
        });
        Assert.Equal(events.Count, events.Select(e => e["UID"]).Distinct().Count());
        Assert.All(events, e => Assert.Equal("20261012T080000Z", e["DTSTAMP"]));

        // In oktober is het zomertijd (UTC+2), in december wintertijd (UTC+1).
        var meeting = Ics.Titled(body, IcsWriter.Escape(title));
        Assert.Equal(("20261014T080000Z", "20261014T093000Z"), (meeting["DTSTART"], meeting["DTEND"]));
        Assert.Equal("Utrecht\\, kantoor\\; 2e verdieping", meeting["LOCATION"]);
        Assert.Equal("Klant: Bakkerij\\, Jansen & Zn\\; B.V.", meeting["DESCRIPTION"]);
        Assert.Equal("Afspraak", meeting["CATEGORIES"]);
        Assert.Equal(("20261201T080000Z", "20261201T084500Z"), (Ics.Titled(body, "Winteroverleg")["DTSTART"], Ics.Titled(body, "Winteroverleg")["DTEND"]));

        // Hele dagen als datum, met een einde dat niet meer meetelt.
        var deadline = Ics.Titled(body, "Deadline Webshop");
        Assert.Equal(("20261120", "20261121"), (deadline["DTSTART;VALUE=DATE"], deadline["DTEND;VALUE=DATE"]));
        var invoice = Assert.Single(events, e => e["SUMMARY"].StartsWith("Factuur "));
        Assert.Equal("20261026", invoice["DTSTART;VALUE=DATE"]);
        Assert.Equal("20261111", Assert.Single(events, e => e["SUMMARY"].StartsWith("Offerte "))["DTSTART;VALUE=DATE"]);
        Assert.Equal("20261031", Ics.Titled(body, "Btw-aangifte Q3 2026")["DTSTART;VALUE=DATE"]);
        // Het venster begint 90 dagen terug: de aangifte over Q2 (31 juli) staat erin, die over Q1 (30 april) niet meer.
        Assert.Equal("20260731", Ics.Titled(body, "Btw-aangifte Q2 2026")["DTSTART;VALUE=DATE"]);
        Assert.DoesNotContain("Btw-aangifte Q1 2026", body);
        Assert.Equal("20271031", Ics.Titled(body, "Btw-aangifte Q3 2027")["DTSTART;VALUE=DATE"]);

        // Afgevinkte taken en wat buiten het venster valt niet; een afspraak die al geweest is wel.
        Assert.DoesNotContain("Afgevinkte taak", body);
        Assert.DoesNotContain("Lang geleden", body);
        Assert.Contains("SUMMARY:Gedane afspraak", body);

        // Zonder Bedragen en notities tonen: geen bedragen en geen notities.
        Assert.DoesNotContain("Geheime notitie", body);
        Assert.DoesNotContain("€", body);
        Assert.DoesNotContain("Nog open", body);

        // Een tweede keer ophalen: dezelfde UID's. Een HEAD geeft hetzelfde type maar telt niet mee.
        factory.Clock.Value = factory.Clock.Value.AddHours(1);
        var again = await FeedAsync(path);
        Assert.Equal(events.Select(e => e["UID"]).Order(), Ics.Events(again).Select(e => e["UID"]).Order());
        Assert.Contains("DTSTAMP:20261012T090000Z", again);
        var head = await Calendar().SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal("text/calendar", head.Content.Headers.ContentType!.MediaType);
        var feed = await s.Admin.GetFromJsonAsync<JsonElement>("/api/calendar-feed");
        Assert.Equal(2, feed.GetProperty("fetches").GetInt32());
        Assert.Equal(new DateTime(2026, 10, 12, 9, 0, 0), feed.GetProperty("lastFetchedAt").GetDateTime());
    }

    [Fact]
    public async Task Amounts_and_notes_only_when_switched_on()
    {
        var s = await ReadyAsync("ics-bedragen");
        await AppointmentAsync(s.Admin, "Overleg", "2026-10-14T10:00", "2026-10-14T11:00", notes: "Prijs besproken: 95 per uur");
        await MoneyAsync(s);
        var path = await LinkAsync(s.Admin);
        Assert.DoesNotContain("Prijs besproken", await FeedAsync(path));

        var on = await OptionsAsync(s.Admin, showAmounts: true);
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.True((await on.JsonAsync()).GetProperty("showAmounts").GetBoolean());
        var body = Ics.Unfold(await FeedAsync(path));
        Assert.Contains("Prijs besproken: 95 per uur", body);
        // De komma in een bedrag krijgt in de agenda een backslash.
        Assert.Matches(@"Nog open: €\s242\\,00", body);
        Assert.Matches(@"Totaal: €\s1\.210\\,00", body);

        // Onderdelen uitzetten haalt ze uit de agenda.
        Assert.Equal(HttpStatusCode.OK, (await OptionsAsync(s.Admin, planning: false, invoices: false, quotes: false, vat: false, showAmounts: true)).StatusCode);
        var only = Ics.Events(await FeedAsync(path));
        Assert.Equal(new[] { "Deadline Webshop" }, only.Select(e => e["SUMMARY"]));
    }

    [Fact]
    public async Task Only_the_hash_is_stored_and_every_bad_link_gives_the_same_404()
    {
        var s = await ReadyAsync("ics-token");
        await AppointmentAsync(s.Admin, "Mijn afspraak", "2026-10-14T10:00", "2026-10-14T11:00");
        var path = await LinkAsync(s.Admin);
        var token = TokenOf(path);

        // Alleen de hash staat in de database; het token zelf nergens, ook niet in de export of de instellingen.
        var row = factory.WithDb(db => db.CalendarFeeds.IgnoreQueryFilters().AsNoTracking().Single(f => f.WorkspaceId == s.Ws));
        Assert.Equal(InvoiceMailer.Hash(token), row.TokenHash);
        Assert.NotEqual(token, row.TokenHash);
        Assert.DoesNotContain(token, JsonSerializer.Serialize(row));
        var settings = await s.Admin.GetStringAsync("/api/calendar-feed");
        Assert.DoesNotContain(token, settings);
        Assert.DoesNotContain(row.TokenHash!, settings);
        Assert.DoesNotContain("tokenHash", settings, StringComparison.OrdinalIgnoreCase);
        var export = await s.Admin.GetStringAsync("/api/workspace/export");
        Assert.Contains("\"calendarFeeds\"", export);
        Assert.DoesNotContain(row.TokenHash!, export);
        Assert.DoesNotContain(token, export);

        // Werkt zonder cookie.
        Assert.Contains("Mijn afspraak", await FeedAsync(path));

        async Task NotFound(string p)
        {
            var res = await Calendar().GetAsync(p);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.Equal("", await res.Content.ReadAsStringAsync());
            Assert.Null(res.Content.Headers.ContentType);
        }
        var wrong = token[..^4] + (token.EndsWith("AAAA") ? "BBBB" : "AAAA");
        await NotFound($"/api/public/agenda/{wrong}.ics");
        await NotFound($"/api/public/agenda/{new string('a', 150)}.ics");
        await NotFound($"/api/public/agenda/{row.TokenHash}.ics");

        // Een nieuwe link: de oude werkt niet meer, de nieuwe wel.
        var renewed = await LinkAsync(s.Admin);
        Assert.NotEqual(path, renewed);
        await NotFound(path);
        Assert.Contains("Mijn afspraak", await FeedAsync(renewed));

        // Ingetrokken: weg.
        var revoked = await s.Admin.PostAsync("/api/calendar-feed/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.False((await revoked.JsonAsync()).GetProperty("active").GetBoolean());
        await NotFound(renewed);
        Assert.Null(factory.WithDb(db => db.CalendarFeeds.IgnoreQueryFilters().Single(f => f.WorkspaceId == s.Ws).TokenHash));
    }

    [Fact]
    public async Task A_disabled_or_deleted_user_loses_the_link()
    {
        var s = await ReadyAsync("ics-gebruiker");
        var email = $"ics-mw-{Guid.NewGuid():N}@example.com";
        var employeeId = await TestApi.CreateUserAsync(s.Admin, email);
        var employee = await TestApi.LoginAsync(factory, email);
        await AppointmentAsync(s.Admin, "Voor de medewerker", "2026-10-14T10:00", "2026-10-14T11:00", employeeId);
        var path = await LinkAsync(employee);
        Assert.Contains("Voor de medewerker", await FeedAsync(path));

        // Uitgeschakeld: de link is ingetrokken en werkt ook na weer inschakelen niet meer.
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PutAsJsonAsync($"/api/users/{employeeId}", new { name = "Medewerker", email, role = "medewerker", active = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Calendar().GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PutAsJsonAsync($"/api/users/{employeeId}", new { name = "Medewerker", email, role = "medewerker", active = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Calendar().GetAsync(path)).StatusCode);

        // Ook als de rij nog een hash heeft (een oudere stand): wie niet actief is, krijgt niets.
        var again = await LinkAsync(await TestApi.LoginAsync(factory, email));
        factory.WithDb(db => db.Users.IgnoreQueryFilters().Where(u => u.Id == employeeId).ExecuteUpdate(x => x.SetProperty(u => u.Active, false)));
        Assert.Equal(HttpStatusCode.NotFound, (await Calendar().GetAsync(again)).StatusCode);
        factory.WithDb(db => db.Users.IgnoreQueryFilters().Where(u => u.Id == employeeId).ExecuteUpdate(x => x.SetProperty(u => u.Active, true)));
        Assert.Equal(HttpStatusCode.OK, (await Calendar().GetAsync(again)).StatusCode);

        // Verwijderd: de link is weg, en wat voor hem stond is daarna voor iedereen.
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.DeleteAsync($"/api/users/{employeeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Calendar().GetAsync(again)).StatusCode);
        Assert.False(factory.WithDb(db => db.CalendarFeeds.IgnoreQueryFilters().Any(f => f.UserId == employeeId)));
        var list = await s.Admin.GetFromJsonAsync<List<JsonElement>>("/api/appointments");
        Assert.Equal(JsonValueKind.Null, list!.Single(a => a.GetProperty("title").GetString() == "Voor de medewerker").GetProperty("userId").ValueKind);
    }

    [Fact]
    public async Task Each_user_sees_their_own_planning_and_nothing_from_another_workspace()
    {
        var a = await ReadyAsync("ics-iso-a");
        var b = await ReadyAsync("ics-iso-b");
        var email = $"ics-iso-mw-{Guid.NewGuid():N}@example.com";
        var employeeId = await TestApi.CreateUserAsync(a.Admin, email);
        var employee = await TestApi.LoginAsync(factory, email);
        var inactiveEmail = $"ics-iso-weg-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(a.Admin, inactiveEmail, active: false);

        await AppointmentAsync(a.Admin, "Alleen de beheerder", "2026-10-14T10:00", "2026-10-14T11:00", a.AdminId);
        await AppointmentAsync(employee, "Alleen de medewerker", "2026-10-14T12:00", "2026-10-14T13:00", employeeId);
        await AppointmentAsync(employee, "Voor iedereen", "2026-10-15T09:00", "2026-10-15T10:00");
        await AppointmentAsync(b.Admin, "Andere werkruimte", "2026-10-14T10:00", "2026-10-14T11:00", b.AdminId);

        // Inplannen voor iemand van buiten je werkruimte of iemand die niet bestaat, kan niet.
        foreach (var userId in new[] { b.AdminId, 999_999 })
        {
            var res = await a.Admin.PostAsJsonAsync("/api/appointments", new { title = "Fout", start = "2026-10-14T10:00", end = "2026-10-14T11:00", kind = "afspraak", userId });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains("hoort niet bij je werkruimte", await res.ErrorAsync());
        }
        var mine = await AppointmentAsync(a.Admin, "Wordt verplaatst", "2026-10-16T10:00", "2026-10-16T11:00", a.AdminId);
        var put = await a.Admin.PutAsJsonAsync($"/api/appointments/{mine}", new { title = "Wordt verplaatst", start = "2026-10-16T10:00", end = "2026-10-16T11:00", kind = "afspraak", userId = b.AdminId });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);

        // Wie je kunt kiezen: de actieve mensen uit je eigen werkruimte, ook voor een medewerker.
        var people = await employee.GetFromJsonAsync<List<JsonElement>>("/api/appointments/people");
        Assert.Equal(new[] { a.AdminId, employeeId }.Order(), people!.Select(p => p.GetProperty("id").GetInt32()).Order());
        Assert.All(people!, p => Assert.Equal(new[] { "id", "name" }, p.EnumerateObject().Select(x => x.Name)));

        var adminPath = await LinkAsync(a.Admin);
        var employeePath = await LinkAsync(employee);
        var bPath = await LinkAsync(b.Admin);
        var adminFeed = await FeedAsync(adminPath);
        var employeeFeed = await FeedAsync(employeePath);
        var bFeed = await FeedAsync(bPath);
        Assert.Equal(new[] { "Alleen de beheerder", "Voor iedereen", "Wordt verplaatst" },
            Ics.Events(adminFeed).Where(e => e["CATEGORIES"] == "Afspraak").Select(e => e["SUMMARY"]).Order());
        Assert.Equal(new[] { "Alleen de medewerker", "Voor iedereen" }, Ics.Events(employeeFeed).Select(e => e["SUMMARY"]).Order());
        Assert.Equal(new[] { "Andere werkruimte" }, Ics.Events(bFeed).Where(e => e["CATEGORIES"] == "Afspraak").Select(e => e["SUMMARY"]));

        // Met een cookie van een andere werkruimte erbij blijft het de agenda van de link.
        var withCookie = await b.Admin.GetStringAsync(adminPath);
        Assert.Contains("Alleen de beheerder", withCookie);
        Assert.DoesNotContain("Andere werkruimte", withCookie);

        // Iedereen beheert alleen zijn eigen link: intrekken door de medewerker laat die van de beheerder werken.
        Assert.Equal(HttpStatusCode.OK, (await employee.PostAsync("/api/calendar-feed/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Calendar().GetAsync(employeePath)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Calendar().GetAsync(adminPath)).StatusCode);
        Assert.True((await a.Admin.GetFromJsonAsync<JsonElement>("/api/calendar-feed")).GetProperty("active").GetBoolean());

        // Zonder inloggen kun je niets aan een link doen.
        var anonymous = Calendar();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/calendar-feed")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/calendar-feed/link", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/calendar-feed/revoke", null)).StatusCode);
    }

    [Fact]
    public async Task Money_is_only_in_the_agenda_of_an_admin()
    {
        var s = await ReadyAsync("ics-rol");
        await MoneyAsync(s);
        var email = $"ics-rol-mw-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(s.Admin, email);
        var employee = await TestApi.LoginAsync(factory, email);

        // Een medewerker kan facturen, offertes en btw niet aanzetten.
        var fresh = await employee.GetFromJsonAsync<JsonElement>("/api/calendar-feed");
        Assert.False(fresh.GetProperty("canSeeMoney").GetBoolean());
        Assert.False(fresh.GetProperty("invoices").GetBoolean());
        var set = await (await OptionsAsync(employee, showAmounts: true)).JsonAsync();
        Assert.Equal((false, false, false, true), (set.GetProperty("invoices").GetBoolean(), set.GetProperty("quotes").GetBoolean(), set.GetProperty("vat").GetBoolean(),
            set.GetProperty("deadlines").GetBoolean()));
        var employeeFeed = await FeedAsync(await LinkAsync(employee));
        Assert.Equal(new[] { "Deadline Webshop" }, Ics.Events(employeeFeed).Select(e => e["SUMMARY"]));
        Assert.DoesNotContain("€", employeeFeed);

        // De beheerder ziet alles.
        var adminPath = await LinkAsync(s.Admin);
        var adminFeed = await FeedAsync(adminPath);
        Assert.Contains(Ics.Events(adminFeed), e => e["CATEGORIES"] == "Factuur");
        Assert.Contains(Ics.Events(adminFeed), e => e["CATEGORIES"] == "Offerte");
        Assert.Contains(Ics.Events(adminFeed), e => e["CATEGORIES"] == "Btw");

        // Een aangifte die je als gedaan markeerde, staat er nog wel, met die opmerking.
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PutAsJsonAsync("/api/tax/vat-return/filed", new { year = 2026, quarter = 3, filed = true })).StatusCode);
        Assert.Contains("als gedaan gemarkeerd", Ics.Titled(await FeedAsync(adminPath), "Btw-aangifte Q3 2026")["DESCRIPTION"]);

        // Geen beheerder meer: bij de volgende keer ophalen is het geld weg, met dezelfde link.
        var other = $"ics-rol-bh-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(s.Admin, other, role: "beheerder");
        var me = await s.Admin.GetFromJsonAsync<JsonElement>("/api/auth/me");
        var demote = await s.Admin.PutAsJsonAsync($"/api/users/{s.AdminId}", new { name = me.GetProperty("name").GetString(), email = me.GetProperty("email").GetString(), role = "medewerker", active = true });
        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
        var demoted = await FeedAsync(adminPath);
        Assert.DoesNotContain(Ics.Events(demoted), e => e["CATEGORIES"] is "Factuur" or "Offerte" or "Btw");
        Assert.Contains(Ics.Events(demoted), e => e["CATEGORIES"] == "Deadline");
    }

    [Fact]
    public async Task Demo_data_never_ends_up_in_the_agenda()
    {
        // De standaardwerkruimte heeft voorbeelddata, met afspraken in deze week.
        var admin = await factory.LoginAsync();
        Assert.Contains(await admin.GetFromJsonAsync<List<JsonElement>>("/api/appointments") ?? [], a => a.GetProperty("title").GetString() == "Kennismaking Visser Media");
        var body = await FeedAsync(await LinkAsync(admin));
        Assert.DoesNotContain("Visser Media", body);
        Assert.DoesNotContain(Ics.Events(body), e => e["CATEGORIES"] is "Factuur" or "Offerte");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/calendar-feed/revoke", null)).StatusCode);
    }

    [Fact]
    public async Task A_read_only_workspace_keeps_its_agenda_and_can_revoke_but_not_make_links()
    {
        var s = await ReadyAsync("ics-alleen-lezen");
        await AppointmentAsync(s.Admin, "Nog steeds zichtbaar", "2026-10-14T10:00", "2026-10-14T11:00");
        var path = await LinkAsync(s.Admin);
        await TestApi.ExpireTrialAsync(factory, s.Ws);

        Assert.Contains("Nog steeds zichtbaar", await FeedAsync(path));
        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Admin.PostAsync("/api/calendar-feed/link", null)).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await OptionsAsync(s.Admin, showAmounts: true)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.GetAsync("/api/calendar-feed")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Admin.PostAsync("/api/calendar-feed/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Calendar().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_workspace_removes_the_links()
    {
        var s = await ReadyAsync("ics-weg");
        var path = await LinkAsync(s.Admin);
        Assert.Equal(HttpStatusCode.OK, (await Calendar().GetAsync(path)).StatusCode);
        var res = await s.Admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) });
        Assert.True(res.StatusCode == HttpStatusCode.NoContent, $"{res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        Assert.False(factory.WithDb(db => db.CalendarFeeds.IgnoreQueryFilters().Any(f => f.WorkspaceId == s.Ws)));
        Assert.Equal(HttpStatusCode.NotFound, (await Calendar().GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData("Google-Calendar-Importer", "Google Agenda")]
    [InlineData("Microsoft Office/16.0 (Windows NT 10.0; Microsoft Outlook 16.0.17928; Pro)", "Outlook")]
    [InlineData("iOS/18.0 (22A3354) dataaccessd/1.0", "Apple Agenda op een iPhone of iPad")]
    [InlineData("macOS/15.0 (24A335) CalendarAgent/1000", "Apple Agenda op een Mac")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) Firefox/131.0", "een webbrowser")]
    [InlineData("curl/8.5.0", "een andere agenda")]
    [InlineData("", null)]
    public void The_calendar_that_fetched_the_link_gets_a_readable_name(string ua, string? expected) =>
        Assert.Equal(expected, CalendarFeedEndpoints.ClientName(ua));
}

public class CalendarFeedRateLimitTests(StrictCalendarFactory factory) : IClassFixture<StrictCalendarFactory>
{
    [Fact]
    public async Task Fetching_is_limited_per_address()
    {
        var c = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/public/agenda/onbekend{i}.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await c.GetAsync("/api/public/agenda/onbekend.ics")).StatusCode);
    }
}

// De migratie AgendaFeed op een bestaande database: afspraken blijven staan (voor iedereen) en er zijn nog geen links.
public class AgendaFeedMigrationTests : IDisposable
{
    const string Before = "20261017010701_VasteKosten";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-agenda-{Guid.NewGuid():N}.db");

    AppDb Db() => new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant { WorkspaceId = 1 });

    [Fact]
    public void Existing_appointments_survive_for_everyone()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 'Oud bedrijf', 'team');
                INSERT INTO Users (Id, WorkspaceId, Name, Email, PasswordHash, Role, Active, CreatedAt, SecurityStamp, IsPlatformAdmin)
                    VALUES (1, 1, 'Eigenaar', 'oud@example.com', 'x', 'beheerder', 1, '2026-01-01 00:00:00', 'stempel', 0);
                INSERT INTO Appointments (Id, WorkspaceId, IsDemo, Title, Start, End, Kind, Done)
                    VALUES (1, 1, 0, 'Bestaande afspraak', '2026-10-14 10:00:00', '2026-10-14 11:00:00', 'afspraak', 0);
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = Db())
        {
            db.GetService<IMigrator>().Migrate();
            var a = db.Appointments.Single();
            Assert.Equal(("Bestaande afspraak", (int?)null), (a.Title, a.UserId));
            Assert.Empty(db.CalendarFeeds);
            db.CalendarFeeds.Add(new CalendarFeed { UserId = 1, TokenHash = "hash" });
            db.SaveChanges();
            var f = db.CalendarFeeds.Single();
            Assert.Equal((1, true, false), (f.WorkspaceId, f.Planning, f.ShowAmounts));
            // Eén link per gebruiker.
            db.CalendarFeeds.Add(new CalendarFeed { UserId = 1 });
            Assert.Throws<DbUpdateException>(() => db.SaveChanges());
        }
    }

    public void Dispose()
    {
        TestDatabase.Delete(path);
    }
}

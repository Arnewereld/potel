using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Potel.Api.Data;
using Potel.Api.Invoicing;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

public class HoursFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:PublicInvoicePerMinute", "10000");
    }
}

// De urenspecificatie bij een factuur: per project de boekingen, met een totaal dat gelijk is aan de urenregels,
// vastgelegd bij versturen en alleen zichtbaar binnen de eigen werkruimte en via de link van die ene factuur.
public class HourSpecificationTests(HoursFactory factory) : IClassFixture<HoursFactory>
{
    record Setup(HttpClient Client, int Workspace, int Customer, int ProjectA, int ProjectB, int InvoiceId);

    // Twee projecten met uren in vreemde minuten, één niet-factureerbare boeking, en een factuur met A in één regel en B per boeking.
    async Task<Setup> SetupAsync(string prefix)
    {
        var c = await ReadyAsync(factory, prefix);
        var ws = await TestApi.WorkspaceIdAsync(c);
        var customer = await CustomerAsync(c);
        var a = await ProjectAsync(c, customer, 95, "Webshop");
        var b = await ProjectAsync(c, customer, 80, "App");
        async Task Book(int project, string date, int minutes, string description, bool billable = true) =>
            Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/time", new { projectId = project, date, minutes, description, billable })).StatusCode);
        await Book(a, "2026-10-01", 20, "Checkout bouwen");
        await Book(a, "2026-10-02", 20, "Betaalprovider koppelen");
        await Book(a, "2026-10-03", 20, "Tests schrijven");
        await Book(a, "2026-10-03", 45, "Intern overleg", billable: false);
        await Book(b, "2026-10-05", 37, "Inlogscherm");
        await Book(b, "2026-10-06", 52, "Pushmeldingen");
        var res = await c.PostAsJsonAsync($"/api/projects/{a}/invoice", new { detailed = false });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await Json(res)).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/invoices/{id}/hours", new { projectId = b, detailed = true })).StatusCode);
        return new Setup(c, ws, customer, a, b, id);
    }

    static Task<JsonElement> HoursAsync(HttpClient c, int id) => c.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}/hours");

    // De factuur terugsturen zoals hij is, met andere waarden voor een paar velden.
    static async Task<HttpResponseMessage> PutAsync(HttpClient c, int id, Action<Dictionary<string, object?>> change)
    {
        var inv = JsonSerializer.Deserialize<Dictionary<string, object?>>(await c.GetStringAsync($"/api/invoices/{id}"))!;
        change(inv);
        return await c.PutAsJsonAsync($"/api/invoices/{id}", inv);
    }

    static List<JsonElement> Projects(JsonElement spec) => spec.GetProperty("projects").EnumerateArray().ToList();

    static string NewLinkToken(WebApplicationFactory<Program> f, int workspace, int invoiceId)
    {
        var token = InvoiceMailer.NewToken();
        WorkflowKit.InWorkspace(f, workspace, db =>
        {
            db.InvoiceLinks.Add(new InvoiceLink { InvoiceId = invoiceId, TokenHash = InvoiceMailer.Hash(token) });
            return db.SaveChanges();
        });
        return token;
    }

    Task<HttpResponseMessage> PublicAsync(string token, string path = "/api/public/invoice") => factory.CreateClient().PostAsJsonAsync(path, new { token });

    [Fact]
    public async Task The_specification_lists_the_hours_per_project_and_adds_up_to_the_hour_lines()
    {
        var s = await SetupAsync("uren-spec");
        var hours = await HoursAsync(s.Client, s.InvoiceId);
        Assert.True(hours.GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, hours.GetProperty("problem").ValueKind);
        var spec = hours.GetProperty("spec");
        var projects = Projects(spec);
        Assert.Equal(["App", "Webshop"], projects.Select(p => p.GetProperty("name").GetString()));

        // Webshop: drie keer 20 minuten op één regel van 1 uur, verdeeld zodat het precies optelt.
        var webshop = projects[1];
        Assert.Equal([0.33m, 0.34m, 0.33m], webshop.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("hours").GetDecimal()));
        Assert.Equal(1.00m, webshop.GetProperty("hours").GetDecimal());
        Assert.Equal(["Checkout bouwen", "Betaalprovider koppelen", "Tests schrijven"],
            webshop.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("description").GetString()));
        // App: per boeking, 37 en 52 minuten.
        Assert.Equal([0.62m, 0.86m], projects[0].GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("hours").GetDecimal()));
        Assert.Equal(1.48m, projects[0].GetProperty("hours").GetDecimal());

        var invoice = await GetInvoiceAsync(s.Client, s.InvoiceId);
        Assert.Equal(HourQuantity(invoice), spec.GetProperty("hours").GetDecimal());
        Assert.Equal(2.48m, spec.GetProperty("hours").GetDecimal());

        // Niet-factureerbare uren staan er nooit op; zonder team geen namen en standaard geen bedragen.
        Assert.DoesNotContain("Intern overleg", spec.GetRawText());
        Assert.False(spec.GetProperty("showWho").GetBoolean());
        Assert.False(spec.GetProperty("showAmounts").GetBoolean());
        Assert.All(projects.SelectMany(p => p.GetProperty("entries").EnumerateArray()), e => Assert.Equal(JsonValueKind.Null, e.GetProperty("amount").ValueKind));
    }

    [Fact]
    public async Task Amounts_per_entry_appear_only_when_turned_on_and_add_up_to_the_hour_lines()
    {
        var s = await SetupAsync("uren-bedragen");
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(s.Client, s.InvoiceId, inv => inv["hourAmounts"] = true)).StatusCode);
        var spec = (await HoursAsync(s.Client, s.InvoiceId)).GetProperty("spec");
        Assert.True(spec.GetProperty("showAmounts").GetBoolean());
        var projects = Projects(spec);
        Assert.Equal([31.35m, 32.30m, 31.35m], projects[1].GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("amount").GetDecimal()));
        Assert.Equal(95m, projects[1].GetProperty("amount").GetDecimal());
        Assert.Equal(118.40m, projects[0].GetProperty("amount").GetDecimal());

        var invoice = await GetInvoiceAsync(s.Client, s.InvoiceId);
        var hourLines = invoice.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("unit").GetString() == "uur")
            .Sum(l => Math.Round(l.GetProperty("quantity").GetDecimal() * l.GetProperty("unitPrice").GetDecimal(), 2, MidpointRounding.AwayFromZero));
        Assert.Equal(hourLines, spec.GetProperty("amount").GetDecimal());

        // Een regel die niet per uur is (bijvoorbeeld omgezet naar een dag) krijgt geen bedragen per boeking.
        var put = await PutAsync(s.Client, s.InvoiceId, inv =>
        {
            var lines = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(JsonSerializer.Serialize(inv["lines"]))!;
            var first = lines.First(l => l["description"]!.ToString()!.StartsWith("Webshop"));
            first["unit"] = "dag";
            inv["lines"] = lines;
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var after = (await HoursAsync(s.Client, s.InvoiceId)).GetProperty("spec");
        Assert.All(Projects(after).Single(p => p.GetProperty("name").GetString() == "Webshop").GetProperty("entries").EnumerateArray(),
            e => Assert.Equal(JsonValueKind.Null, e.GetProperty("amount").ValueKind));
    }

    [Fact]
    public async Task In_a_team_the_specification_shows_who_booked_the_hours()
    {
        var c = await ReadyAsync(factory, "uren-team");
        var customer = await CustomerAsync(c);
        var project = await ProjectAsync(c, customer, 100, "Portaal");
        var email = $"collega-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(c, email);
        var colleague = await TestApi.LoginAsync(factory, email);
        await TimeAsync(c, project, 60, "2026-10-01");
        // Wat de browser als gebruiker meestuurt telt niet: de boeking is van wie er ingelogd is.
        Assert.Equal(HttpStatusCode.Created, (await colleague.PostAsJsonAsync("/api/time",
            new { projectId = project, date = "2026-10-02", minutes = 30, description = "Review", billable = true, userId = 1 })).StatusCode);
        var id = (await Json(await c.PostAsJsonAsync($"/api/projects/{project}/invoice", new { detailed = false }))).GetProperty("id").GetInt32();
        var spec = (await HoursAsync(c, id)).GetProperty("spec");
        Assert.True(spec.GetProperty("showWho").GetBoolean());
        var who = Projects(spec).Single().GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("who").GetString()).ToList();
        Assert.Equal(["Eigenaar", email.Split('@')[0]], who);
    }

    [Fact]
    public async Task Sending_freezes_the_specification_and_later_changes_to_hours_or_projects_do_not_change_it()
    {
        var s = await SetupAsync("uren-vast");
        var before = (await HoursAsync(s.Client, s.InvoiceId)).GetProperty("spec").GetRawText();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(s.Client, s.InvoiceId, "verzonden")).StatusCode);
        var number = (await GetInvoiceAsync(s.Client, s.InvoiceId)).GetProperty("number").GetString();
        Assert.Equal(before, (await HoursAsync(s.Client, s.InvoiceId)).GetProperty("spec").GetRawText());
        Assert.NotNull(WorkflowKit.InWorkspace(factory, s.Workspace, db => db.Invoices.Single(i => i.Id == s.InvoiceId).HoursSnapshot));

        // Helemaal crediteren geeft de uren vrij; daarna veranderen boekingen en projecten.
        var credit = (await Json(await s.Client.PostAsync($"/api/invoices/{s.InvoiceId}/credit", null))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(s.Client, credit, "verzonden")).StatusCode);
        var entries = (await s.Client.GetFromJsonAsync<List<JsonElement>>($"/api/time?projectId={s.ProjectA}"))!;
        var first = entries.First(e => e.GetProperty("billable").GetBoolean());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("invoiceId").ValueKind);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PutAsJsonAsync($"/api/time/{first.GetProperty("id").GetInt32()}",
            new { projectId = s.ProjectA, date = "2026-10-09", minutes = 240, description = "Iets heel anders", billable = true })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PutAsJsonAsync($"/api/projects/{s.ProjectA}",
            new { name = "Nieuwe naam", customerId = s.Customer, status = "actief", billing = "uur", hourlyRate = 95, color = "#000000" })).StatusCode);
        foreach (var e in entries.Where(e => e.GetProperty("billable").GetBoolean()).Skip(1))
            Assert.Equal(HttpStatusCode.NoContent, (await s.Client.DeleteAsync($"/api/time/{e.GetProperty("id").GetInt32()}")).StatusCode);

        Assert.Equal(before, (await HoursAsync(s.Client, s.InvoiceId)).GetProperty("spec").GetRawText());
        // Ook de klant ziet via zijn link de vastgelegde specificatie.
        var token = NewLinkToken(factory, s.Workspace, s.InvoiceId);
        var page = await (await PublicAsync(token)).JsonAsync();
        Assert.Equal(number, page.GetProperty("invoice").GetProperty("number").GetString());
        var shown = page.GetProperty("hours");
        Assert.Equal(2.48m, shown.GetProperty("hours").GetDecimal());
        Assert.Contains("Checkout bouwen", shown.GetRawText());
        Assert.DoesNotContain("Iets heel anders", shown.GetRawText());
        Assert.DoesNotContain("Nieuwe naam", shown.GetRawText());
        // En de factuur zelf ligt ook vast.
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(s.Client, s.InvoiceId, inv => inv["hourSpecification"] = false)).StatusCode);
    }

    [Fact]
    public async Task An_edited_hour_line_blocks_sending_with_the_specification_until_it_is_turned_off()
    {
        var s = await SetupAsync("uren-afwijking");
        var put = await PutAsync(s.Client, s.InvoiceId, inv =>
        {
            var lines = JsonSerializer.Deserialize<List<Dictionary<string, object?>>>(JsonSerializer.Serialize(inv["lines"]))!;
            lines.First(l => l["description"]!.ToString()!.StartsWith("Webshop"))["quantity"] = 0.5m;
            inv["lines"] = lines;
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var problem = (await HoursAsync(s.Client, s.InvoiceId)).GetProperty("problem").GetString();
        Assert.Contains("0,5 uur", problem);
        Assert.Contains("samen 1 uur", problem);

        var send = await SetStatusAsync(s.Client, s.InvoiceId, "verzonden");
        Assert.Equal(HttpStatusCode.BadRequest, send.StatusCode);
        Assert.Equal(problem, await send.ErrorAsync());
        Assert.Equal("concept", (await GetInvoiceAsync(s.Client, s.InvoiceId)).GetProperty("status").GetString());

        // Zonder specificatie mag het wel, en dan krijgt de klant er ook geen.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(s.Client, s.InvoiceId, inv => inv["hourSpecification"] = false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(s.Client, s.InvoiceId, "verzonden")).StatusCode);
        Assert.Null(WorkflowKit.InWorkspace(factory, s.Workspace, db => db.Invoices.Single(i => i.Id == s.InvoiceId).HoursSnapshot));
        var token = NewLinkToken(factory, s.Workspace, s.InvoiceId);
        Assert.Equal(JsonValueKind.Null, (await (await PublicAsync(token)).JsonAsync()).GetProperty("hours").ValueKind);
        var csv = await PublicAsync(token, "/api/public/invoice/hours");
        Assert.Equal(HttpStatusCode.NotFound, csv.StatusCode);
        Assert.Equal(Endpoints.InvoiceMailEndpoints.LinkGone, await csv.ErrorAsync());
    }

    [Fact]
    public async Task The_csv_has_the_same_rows_and_totals_for_the_freelancer_and_the_client()
    {
        var s = await SetupAsync("uren-csv");
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(s.Client, s.InvoiceId, "verzonden")).StatusCode);
        var number = (await GetInvoiceAsync(s.Client, s.InvoiceId)).GetProperty("number").GetString()!;

        var mine = await s.Client.GetAsync($"/api/invoices/{s.InvoiceId}/hours.csv");
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal("text/csv", mine.Content.Headers.ContentType?.MediaType);
        Assert.Contains($"urenspecificatie-{number}.csv", mine.Content.Headers.ContentDisposition?.ToString());
        var text = Encoding.UTF8.GetString(await mine.Content.ReadAsByteArrayAsync()).TrimStart('﻿');
        Assert.Contains($"Urenspecificatie bij factuur {number};Mijn Bedrijf", text);
        Assert.Contains("Project;Datum;Omschrijving;Uren\r\n", text);
        Assert.Contains("Webshop;01-10-2026;Checkout bouwen;0,33", text);
        Assert.Contains("App;06-10-2026;Pushmeldingen;0,86", text);
        Assert.Contains("Subtotaal Webshop;;;1,00", text);
        Assert.Contains("Totaal;;;2,48", text);
        Assert.DoesNotContain("Intern overleg", text);

        var token = NewLinkToken(factory, s.Workspace, s.InvoiceId);
        var theirs = await PublicAsync(token, "/api/public/invoice/hours");
        Assert.Equal(HttpStatusCode.OK, theirs.StatusCode);
        Assert.Equal("noindex, nofollow", theirs.Headers.GetValues("X-Robots-Tag").Single());
        Assert.Equal(text, Encoding.UTF8.GetString(await theirs.Content.ReadAsByteArrayAsync()).TrimStart('﻿'));

        // Een foute of ingetrokken link geeft hetzelfde antwoord als bij de factuur zelf.
        foreach (var wrong in new[] { token[..^1] + (token[^1] == 'A' ? 'B' : 'A'), "", new string('x', 300) })
        {
            var res = await PublicAsync(wrong, "/api/public/invoice/hours");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.Equal(Endpoints.InvoiceMailEndpoints.LinkGone, await res.ErrorAsync());
        }
        (await s.Client.PostAsync($"/api/invoices/{s.InvoiceId}/links/revoke", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await PublicAsync(token, "/api/public/invoice/hours")).StatusCode);
    }

    [Fact]
    public async Task Another_workspace_never_sees_or_links_these_hours()
    {
        var s = await SetupAsync("uren-eigen");
        var other = await ReadyAsync(factory, "uren-ander");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/invoices/{s.InvoiceId}/hours")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/invoices/{s.InvoiceId}/hours.csv")).StatusCode);

        // Een eigen concept kan geen uren van een project uit een andere werkruimte krijgen.
        var otherCustomer = await CustomerAsync(other);
        var otherInvoice = await InvoiceAsync(other, otherCustomer, "concept", 1, 100);
        var res = await other.PostAsJsonAsync($"/api/invoices/{otherInvoice}/hours", new { projectId = s.ProjectA, detailed = false });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(JsonValueKind.Null, (await HoursAsync(other, otherInvoice)).GetProperty("spec").ValueKind);
        // En de uren van de eerste werkruimte staan nog gewoon op de eigen factuur.
        Assert.Equal(2.48m, (await HoursAsync(s.Client, s.InvoiceId)).GetProperty("spec").GetProperty("hours").GetDecimal());
    }

    [Fact]
    public void Non_billable_entries_and_entries_without_minutes_never_appear()
    {
        var line = new InvoiceLine { Id = 7, Description = "Werk", Quantity = 1.5m, Unit = "uur", UnitPrice = 100 };
        var entries = new[]
        {
            new TimeEntry { Id = 1, ProjectId = 3, Date = new DateTime(2026, 10, 1), Minutes = 90, Description = "Bouwen", Billable = true, InvoiceLineId = 7 },
            new TimeEntry { Id = 2, ProjectId = 3, Date = new DateTime(2026, 10, 1), Minutes = 60, Description = "Lunch", Billable = false, InvoiceLineId = 7 },
        };
        var (spec, problem) = HourSpecs.Build([line], entries, new Dictionary<int, string> { [3] = "Site" }, new Dictionary<int, string>(), team: true, showAmounts: true);
        Assert.Null(problem);
        var entry = Assert.Single(Assert.Single(spec!.Projects).Entries);
        Assert.Equal("Bouwen", entry.Description);
        Assert.Equal(1.5m, spec.Hours);
        Assert.Equal(150m, spec.Amount);
        // Zonder namen bekend (geen team of oude boekingen) geen kolom "Wie".
        Assert.False(spec.ShowWho);
        var (none, noProblem) = HourSpecs.Build([line], [entries[1]], new Dictionary<int, string>(), new Dictionary<int, string>(), false, false);
        Assert.Null(none);
        Assert.Null(noProblem);
    }
}

// De migratie voor de urenspecificatie: verstuurde facturen blijven zonder specificatie, concepten krijgen hem standaard,
// en bestaande uren blijven zoals ze waren.
public class HourSpecificationMigrationTests : IDisposable
{
    const string Before = "20261012161634_GitHubKoppeling";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-migratie-uren-{Guid.NewGuid():N}.db");

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
    public void Existing_invoices_and_hours_survive_and_sent_invoices_stay_without_a_specification()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, WebhooksSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 0, 'Oud bedrijf', 'team');
                INSERT INTO Customers (Id, WorkspaceId, CreatedAt, Name, Country, IsDemo, RemindersOff) VALUES (1, 1, '2026-01-01 00:00:00', 'Jan', 'Nederland', 0, 0);
                INSERT INTO Projects (Id, WorkspaceId, CustomerId, Name, Billing, Color, CreatedAt, FixedPrice, HourlyRate, Status, IsDemo)
                    VALUES (1, 1, 1, 'Website', 'uur', '#000000', '2026-01-01 00:00:00', 0, 95, 'actief', 0);
                INSERT INTO Invoices (Id, WorkspaceId, CustomerId, Number, Status, IssueDate, DueDate, VatRegime, IsDemo, RemindersOff) VALUES
                    (1, 1, 1, '2026-0001', 'verzonden', '2026-09-01 00:00:00', '2026-09-15 00:00:00', 'normaal', 0, 0),
                    (2, 1, 1, NULL, 'concept', '2026-09-05 00:00:00', '2026-09-19 00:00:00', 'normaal', 0, 0);
                INSERT INTO InvoiceLines (Id, InvoiceId, Position, Description, Quantity, Unit, UnitPrice, VatRate) VALUES
                    (1, 1, 0, 'Website', 1, 'uur', 95, 21), (2, 2, 0, 'Website', 0.5, 'uur', 95, 21);
                INSERT INTO TimeEntries (Id, WorkspaceId, ProjectId, Date, Minutes, Description, Billable, CreatedAt, InvoiceId, InvoiceLineId, IsDemo) VALUES
                    (1, 1, 1, '2026-08-20 00:00:00', 60, 'Bouwen', 1, '2026-08-20 00:00:00', 1, 1, 0),
                    (2, 1, 1, '2026-09-02 00:00:00', 30, 'Testen', 1, '2026-09-02 00:00:00', 2, 2, 0);
                """;
            cmd.ExecuteNonQuery();
        }

        using (var db = Db()) db.GetService<IMigrator>().Migrate();

        Assert.Equal(0L, Scalar("SELECT HourSpecification FROM Invoices WHERE Id = 1"));
        Assert.Equal(1L, Scalar("SELECT HourSpecification FROM Invoices WHERE Id = 2"));
        Assert.Equal(0L, Scalar("SELECT HourAmounts FROM Invoices WHERE Id = 2"));
        Assert.Null(Scalar("SELECT HoursSnapshot FROM Invoices WHERE Id = 1"));
        Assert.Equal("2026-0001", Scalar("SELECT Number FROM Invoices WHERE Id = 1"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM TimeEntries WHERE InvoiceLineId IS NOT NULL AND UserId IS NULL"));
        Assert.Equal("Bouwen", Scalar("SELECT Description FROM TimeEntries WHERE Id = 1"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(f)) File.Delete(f);
    }
}

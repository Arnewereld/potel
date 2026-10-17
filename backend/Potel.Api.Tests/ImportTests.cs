using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Importing;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Een testhost met een klok die de test verzet, om het ongedaan maken na 24 uur te testen.
public class ImportFactory : PortalFactory
{
    public MovableClock Clock { get; } = new(DateTime.UtcNow);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(s => s.AddSingleton<BusinessClock>(Clock));
    }
}

static class ImportKit
{
    public static string Data(string csv) => Convert.ToBase64String(Encoding.UTF8.GetBytes(csv));

    public static object Body(string kind, string csv, object? mapping = null, string duplicates = "overslaan", object? customers = null, object? projects = null,
        string fileName = "export.csv") =>
        new { kind, fileName, data = Data(csv), mapping, duplicates, customers, projects };

    public static async Task<JsonElement> PreviewAsync(HttpClient c, string kind, string csv)
    {
        var res = await c.PostAsJsonAsync("/api/imports/preview", new { kind, fileName = "export.csv", data = Data(csv) });
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await res.JsonAsync();
    }

    public static async Task<JsonElement> CheckAsync(HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/api/imports/check", body);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await res.JsonAsync();
    }

    public static async Task<JsonElement> RunAsync(HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/api/imports", body);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await res.JsonAsync();
    }

    public static List<string> Messages(JsonElement check, string list = "errors") =>
        check.GetProperty(list).EnumerateArray().Select(e => $"{e.GetProperty("line").GetInt32()}: {e.GetProperty("text").GetString()}").ToList();

    public static async Task<List<JsonElement>> CustomersAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/customers")).EnumerateArray().ToList();

    public static async Task<List<JsonElement>> ProjectsAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/api/projects")).EnumerateArray().ToList();

    public static async Task<List<JsonElement>> EntriesAsync(HttpClient c, DateTime from, DateTime to) =>
        (await c.GetFromJsonAsync<JsonElement>($"/api/time?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}")).EnumerateArray().ToList();

    public static string D(DateTime d) => d.ToString("dd-MM-yyyy");
}

// Importeren uit een ander pakket: klanten, projecten, uren uit Toggl Track, Clockify en Harvest, en oude facturen. Alles of niets per
// bestand, dubbele regels overslaan of bijwerken, 24 uur ongedaan maken en nooit in een andere werkruimte.
public class ImportTests(ImportFactory f) : IClassFixture<ImportFactory>
{
    static string Mail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@example.com";

    static readonly DateTime Day = DateTime.Today.AddDays(-20);

    [Fact]
    public async Task Customers_are_recognised_validated_per_row_and_imported_all_or_nothing()
    {
        var c = await RegisterAsync(f, Mail("imp-klant"));
        var csv = "Bedrijfsnaam;Contactpersoon;E-mailadres;Telefoon;Adres;Postcode;Plaats;Land;KvK-nummer;Btw-nummer;Taal\n" +
                  "=Fietsplein B.V.;Jan Jansen;jan@fietsplein.nl;+31 6 12345678;Dorpsstraat 1;1234 AB;Utrecht;Nederland;12345678;NL123456789B01;nl\n" +
                  "Studio Noord;;info@studionoord;;;;;NL;1234;NL12;Engels\n" +
                  "Berlin GmbH;;hallo@berlin.de;;;;Berlin;Duitsland;HRB 1234;DE123456789;Klingon\n";

        var preview = await ImportKit.PreviewAsync(c, "klanten", csv);
        var mapping = preview.GetProperty("mapping");
        Assert.Equal("Bedrijfsnaam", mapping.GetProperty("name").GetString());
        Assert.Equal("Contactpersoon", mapping.GetProperty("contact").GetString());
        Assert.Equal("KvK-nummer", mapping.GetProperty("kvk").GetString());
        Assert.Equal("Btw-nummer", mapping.GetProperty("vat").GetString());
        Assert.Equal(3, preview.GetProperty("rows").GetInt32());

        var check = await ImportKit.CheckAsync(c, ImportKit.Body("klanten", csv));
        Assert.False(check.GetProperty("ok").GetBoolean());
        var errors = ImportKit.Messages(check);
        Assert.Contains(errors, e => e.StartsWith("3: Het e-mailadres \"info@studionoord\" klopt niet"));
        Assert.Contains(errors, e => e.StartsWith("3: Het KvK-nummer \"1234\" klopt niet"));
        Assert.Contains(errors, e => e.StartsWith("3: Studio Noord:") && e.Contains("btw"));
        Assert.Contains(errors, e => e.StartsWith("4: De taal \"Klingon\" kennen we niet"));
        Assert.DoesNotContain(errors, e => e.StartsWith("2:"));

        // Met fouten importeert er niets, ook de goede regel niet.
        var refused = await c.PostAsJsonAsync("/api/imports", ImportKit.Body("klanten", csv));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("fouten in het bestand", await refused.ErrorAsync());
        Assert.Empty(await ImportKit.CustomersAsync(c));

        var fixedCsv = csv.Replace("info@studionoord;;;;;NL;1234;NL12", "info@studionoord.nl;;;;;NL;12345679;").Replace("Klingon", "de");
        fixedCsv = fixedCsv.Replace(";de\n", ";\n");
        var result = await ImportKit.RunAsync(c, ImportKit.Body("klanten", fixedCsv));
        Assert.Equal(3, result.GetProperty("created").GetInt32());
        Assert.True(result.GetProperty("canUndo").GetBoolean());

        var customers = await ImportKit.CustomersAsync(c);
        var fiets = customers.Single(x => x.GetProperty("company").GetString() == "Fietsplein B.V.");
        // Het = aan het begin is weg, zodat de naam nooit als formule in een spreadsheet terugkomt; +31 wordt 0031.
        Assert.Equal("Jan Jansen", fiets.GetProperty("name").GetString());
        Assert.Equal("0031 6 12345678", fiets.GetProperty("phone").GetString());
        Assert.Equal("1234 AB Utrecht", fiets.GetProperty("city").GetString());
        Assert.Equal("12345678", fiets.GetProperty("kvk").GetString());
        Assert.Equal("NL123456789B01", fiets.GetProperty("vatNumber").GetString());
        var studio = customers.Single(x => x.GetProperty("name").GetString() == "Studio Noord");
        Assert.Equal("en", studio.GetProperty("language").GetString());
        var berlin = customers.Single(x => x.GetProperty("name").GetString() == "Berlin GmbH");
        Assert.Equal("Duitsland", berlin.GetProperty("country").GetString());
        Assert.Equal("HRB1234", berlin.GetProperty("kvk").GetString());
    }

    [Fact]
    public async Task Duplicate_customers_are_skipped_or_updated_by_name_or_email()
    {
        var c = await RegisterAsync(f, Mail("imp-dubbel"));
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/customers", new { name = "Piet", company = "Bakkerij Piet", email = "piet@bakkerij.nl", city = "Gouda" })).StatusCode);
        var csv = "Naam,E-mail,Plaats,Telefoon\nbakkerij piet,,Utrecht,\nAnders genoemd,PIET@bakkerij.nl,,0612345678\nNieuw B.V.,nieuw@example.com,,\nNieuw B.V.,,,\n";

        var skip = await ImportKit.CheckAsync(c, ImportKit.Body("klanten", csv));
        Assert.True(skip.GetProperty("ok").GetBoolean());
        Assert.Equal(1, skip.GetProperty("create").GetInt32());
        Assert.Equal(3, skip.GetProperty("skip").GetInt32());
        var dups = ImportKit.Messages(skip, "duplicates");
        Assert.Contains(dups, d => d.StartsWith("2: bakkerij piet bestaat al in Potel"));
        Assert.Contains(dups, d => d.StartsWith("3: Anders genoemd bestaat al in Potel"));
        Assert.Contains(dups, d => d.StartsWith("5: Nieuw B.V. staat ook op regel 4"));

        var updated = await ImportKit.RunAsync(c, ImportKit.Body("klanten", csv, duplicates: "bijwerken"));
        Assert.Equal(1, updated.GetProperty("created").GetInt32());
        Assert.Equal(2, updated.GetProperty("updated").GetInt32());
        var piet = (await ImportKit.CustomersAsync(c)).Single(x => x.GetProperty("company").GetString() == "Bakkerij Piet");
        // Bijwerken neemt over wat er in het bestand staat; een lege cel wist niets.
        Assert.Equal("Utrecht", piet.GetProperty("city").GetString());
        Assert.Equal("0612345678", piet.GetProperty("phone").GetString());
        Assert.Equal("piet@bakkerij.nl", piet.GetProperty("email").GetString());
        Assert.Equal(2, (await ImportKit.CustomersAsync(c)).Count);
    }

    [Fact]
    public async Task Toggl_export_with_title_rows_asks_to_confirm_new_customers_and_projects()
    {
        var c = await RegisterAsync(f, Mail("imp-toggl"));
        var existing = await CreateCustomerAsync(c, "Fietsplein");
        var csv = "Toggl Track, Detailed report\nPeriode: vorig jaar\n\n" +
                  "User,Email,Client,Project,Task,Description,Billable,Start date,Start time,End date,End time,Duration,Tags,Amount ()\n" +
                  $"Ann,ann@x.nl,Fietsplein,Webshop,,Bugfix checkout,Yes,{Day:yyyy-MM-dd},09:00:00,{Day:yyyy-MM-dd},10:30:00,01:30:00,,\n" +
                  $"Ann,ann@x.nl,Nieuwe Klant,Website,,=SOM(A1),No,{Day:yyyy-MM-dd},11:00:00,{Day:yyyy-MM-dd},11:20:29,00:20:29,,\n" +
                  $"Ann,ann@x.nl,Nieuwe Klant,Website,,Overleg,Yes,{Day.AddDays(1):yyyy-MM-dd},11:00:00,{Day.AddDays(1):yyyy-MM-dd},13:00:00,02:00:00,,\n";

        var preview = await ImportKit.PreviewAsync(c, "uren", csv);
        Assert.Equal("Toggl Track", preview.GetProperty("source").GetString());
        Assert.Equal(4, preview.GetProperty("headerLine").GetInt32());
        Assert.Equal("Duration", preview.GetProperty("mapping").GetProperty("duration").GetString());
        Assert.Equal("Client", preview.GetProperty("mapping").GetProperty("customer").GetString());

        var check = await ImportKit.CheckAsync(c, ImportKit.Body("uren", csv));
        Assert.Equal(0, check.GetProperty("errorCount").GetInt32());
        Assert.True(check.GetProperty("needsChoice").GetBoolean());
        Assert.False(check.GetProperty("ok").GetBoolean());
        var projects = check.GetProperty("projects").EnumerateArray().ToList();
        Assert.Equal(2, projects.Count);
        var customers = check.GetProperty("customers").EnumerateArray().ToList();
        // Fietsplein is een bestaande klant; Nieuwe Klant nog niet, die moet je bevestigen.
        Assert.Equal(existing, customers.Single(x => x.GetProperty("name").GetString() == "Fietsplein").GetProperty("match").GetInt32());
        Assert.Equal(JsonValueKind.Null, customers.Single(x => x.GetProperty("name").GetString() == "Nieuwe Klant").GetProperty("match").ValueKind);

        var refused = await c.PostAsJsonAsync("/api/imports", ImportKit.Body("uren", csv));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Kies eerst", await refused.ErrorAsync());

        var choices = new Dictionary<string, int> { ["nieuwe klant"] = 0 };
        var projectChoices = new Dictionary<string, int> { ["fietsplein\nwebshop"] = 0, ["nieuwe klant\nwebsite"] = 0 };
        var result = await ImportKit.RunAsync(c, ImportKit.Body("uren", csv, customers: choices, projects: projectChoices));
        Assert.Equal(3, result.GetProperty("created").GetInt32());
        Assert.Equal(2, result.GetProperty("projectsCreated").GetInt32());
        Assert.Equal(1, result.GetProperty("customersCreated").GetInt32());
        Assert.Equal("Toggl Track", result.GetProperty("source").GetString());

        var entries = await ImportKit.EntriesAsync(c, Day, Day.AddDays(1));
        Assert.Equal(3, entries.Count);
        Assert.Contains(entries, e => e.GetProperty("minutes").GetInt32() == 90 && e.GetProperty("billable").GetBoolean());
        // 20 minuten en 29 seconden is 20 minuten; de formule aan het begin is weg.
        var short_ = entries.Single(e => e.GetProperty("minutes").GetInt32() == 20);
        Assert.False(short_.GetProperty("billable").GetBoolean());
        Assert.Equal("SOM(A1)", short_.GetProperty("description").GetString());
        var webshop = (await ImportKit.ProjectsAsync(c)).Single(p => p.GetProperty("name").GetString() == "Webshop");
        Assert.Equal(existing, webshop.GetProperty("customerId").GetInt32());

        // Hetzelfde bestand nog eens: alles staat er al.
        var again = await ImportKit.CheckAsync(c, ImportKit.Body("uren", csv));
        Assert.True(again.GetProperty("needsChoice").GetBoolean() == false, again.ToString());
        Assert.Equal(0, again.GetProperty("create").GetInt32());
        Assert.Equal(3, again.GetProperty("skip").GetInt32());
        var nothing = await c.PostAsJsonAsync("/api/imports", ImportKit.Body("uren", csv));
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
        Assert.Contains("niets te importeren", await nothing.ErrorAsync());
    }

    [Fact]
    public async Task Clockify_and_harvest_exports_with_their_own_durations_and_dates()
    {
        var c = await RegisterAsync(f, Mail("imp-clock"));
        var customer = await CreateCustomerAsync(c, "Studio");
        var project = await InvoiceKit.ProjectAsync(c, customer, 80, "Huisstijl");
        // Clockify met Amerikaanse datums: 10/25 kan alleen maand/dag zijn.
        var us = new DateTime(DateTime.Today.Year - 1, 10, 25);
        var clockify = "Project,Client,Description,Task,User,Group,Email,Tags,Billable,Start Date,Start Time,End Date,End Time,Duration (h),Duration (decimal),Billable Rate (EUR),Billable Amount (EUR)\n" +
                       $"Huisstijl,Studio,Logo,,Ann,,a@x.nl,,Yes,{us:MM/dd/yyyy},09:00 AM,{us:MM/dd/yyyy},10:15 AM,01:15:00,1.25,80.00,100.00\n" +
                       $"Huisstijl,Studio,Kleuren,,Ann,,a@x.nl,,No,{us.AddDays(-20):MM/dd/yyyy},09:00 AM,{us.AddDays(-20):MM/dd/yyyy},09:45 AM,00:45:00,0.75,80.00,60.00\n";
        var preview = await ImportKit.PreviewAsync(c, "uren", clockify);
        Assert.Equal("Clockify", preview.GetProperty("source").GetString());
        Assert.Equal("Duration (h)", preview.GetProperty("mapping").GetProperty("duration").GetString());
        Assert.Equal("Billable Rate (EUR)", preview.GetProperty("mapping").GetProperty("rate").GetString());
        var r1 = await ImportKit.RunAsync(c, ImportKit.Body("uren", clockify));
        Assert.Equal(2, r1.GetProperty("created").GetInt32());
        Assert.Equal(0, r1.GetProperty("projectsCreated").GetInt32());
        var entries = await ImportKit.EntriesAsync(c, us.AddDays(-30), us);
        Assert.Contains(entries, e => e.GetProperty("date").GetString()!.StartsWith(us.ToString("yyyy-MM-dd")) && e.GetProperty("minutes").GetInt32() == 75);
        Assert.Contains(entries, e => e.GetProperty("minutes").GetInt32() == 45 && !e.GetProperty("billable").GetBoolean());
        Assert.All(entries, e => Assert.Equal(project, e.GetProperty("projectId").GetInt32()));

        var harvest = "Date,Client,Project,Project Code,Task,Notes,Hours,Hours Rounded,Billable?,Invoiced?,First Name,Last Name,Billable Rate,Billable Amount,Currency\n" +
                      $"{Day:yyyy-MM-dd},Studio,Huisstijl,,Design,Iconen,\"1,5\",1.5,Yes,No,Ann,B,80,120,Euro\n" +
                      $"{Day:yyyy-MM-dd},Studio,Huisstijl,,Design,Lunch,0,0,No,No,Ann,B,80,0,Euro\n" +
                      $"{DateTime.Today.AddDays(3):yyyy-MM-dd},Studio,Huisstijl,,Design,Later,1,1,Yes,No,Ann,B,80,80,Euro\n" +
                      $"{Day:yyyy-MM-dd},Studio,Huisstijl,,Design,Raar,1:75,1,Misschien,No,Ann,B,80,80,Euro\n";
        Assert.Equal("Harvest", (await ImportKit.PreviewAsync(c, "uren", harvest)).GetProperty("source").GetString());
        var check = await ImportKit.CheckAsync(c, ImportKit.Body("uren", harvest));
        var errors = ImportKit.Messages(check);
        Assert.Contains(errors, e => e.StartsWith("3: De duur is 0"));
        Assert.Contains(errors, e => e.StartsWith("4: De datum") && e.Contains("in de toekomst"));
        Assert.Contains(errors, e => e.StartsWith("5: De duur \"1:75\" kunnen we niet lezen"));
        Assert.Contains(errors, e => e.StartsWith("5: Bij factureerbaar staat \"Misschien\""));
        var good = string.Join('\n', harvest.Split('\n').Take(2)) + "\n";
        Assert.Equal(1, (await ImportKit.RunAsync(c, ImportKit.Body("uren", good))).GetProperty("created").GetInt32());
        Assert.Contains(await ImportKit.EntriesAsync(c, Day, Day), e => e.GetProperty("minutes").GetInt32() == 90 && e.GetProperty("description").GetString() == "Iconen");
    }

    [Fact]
    public async Task Projects_are_imported_with_an_hourly_rate_or_a_fixed_price()
    {
        var c = await RegisterAsync(f, Mail("imp-proj"));
        await CreateCustomerAsync(c, "Fietsplein");
        var csv = "Project;Klant;Uurtarief;Vaste prijs\nApp;Fietsplein;95,00;\nLogo;Nieuw Bureau;;1.250,00\nApp;Fietsplein;100;\nKapot;;-5;\n";
        var check = await ImportKit.CheckAsync(c, ImportKit.Body("projecten", csv));
        var errors = ImportKit.Messages(check);
        Assert.Contains(errors, e => e.StartsWith("5: De klant van dit project ontbreekt"));
        Assert.Contains(errors, e => e.StartsWith("5: Het uurtarief kan niet negatief zijn"));
        var good = csv.Replace("Kapot;;-5;\n", "");
        var result = await ImportKit.RunAsync(c, ImportKit.Body("projecten", good, customers: new Dictionary<string, int> { ["nieuw bureau"] = 0 }));
        Assert.Equal(2, result.GetProperty("created").GetInt32());
        Assert.Equal(1, result.GetProperty("skipped").GetInt32());
        var projects = await ImportKit.ProjectsAsync(c);
        var app = projects.Single(p => p.GetProperty("name").GetString() == "App");
        Assert.Equal("uur", app.GetProperty("billing").GetString());
        Assert.Equal(95m, app.GetProperty("hourlyRate").GetDecimal());
        var logo = projects.Single(p => p.GetProperty("name").GetString() == "Logo");
        Assert.Equal("vast", logo.GetProperty("billing").GetString());
        Assert.Equal(1250m, logo.GetProperty("fixedPrice").GetDecimal());

        // Nog eens met bijwerken: het tarief van het bestaande project gaat mee.
        var again = await ImportKit.RunAsync(c, ImportKit.Body("projecten", "Project;Klant;Uurtarief\nApp;Fietsplein;110\n", duplicates: "bijwerken"));
        Assert.Equal(1, again.GetProperty("updated").GetInt32());
        Assert.Equal(110m, (await ImportKit.ProjectsAsync(c)).Single(p => p.GetProperty("name").GetString() == "App").GetProperty("hourlyRate").GetDecimal());
    }

    [Fact]
    public async Task Imported_invoices_count_as_revenue_but_never_touch_the_number_series_or_the_vat_return()
    {
        var c = await InvoiceKit.ReadyAsync(f, "imp-fact");
        var customer = await InvoiceKit.CustomerAsync(c, "Fietsplein");
        var year = DateTime.Today.Year;
        var jan = new DateTime(year, 1, 15);
        var before = await c.GetFromJsonAsync<JsonElement>($"/api/tax/annual?year={year}");
        var nextBefore = (await c.GetFromJsonAsync<JsonElement>($"/api/invoices/next-number?date={jan:yyyy-MM-dd}")).GetProperty("number").GetString();
        var vatBefore = await c.GetFromJsonAsync<JsonElement>($"/api/tax/vat-return?year={year}&quarter=1");
        var dashBefore = (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("revenueYear").GetDecimal();

        var csv = "Factuurnummer;Factuurdatum;Klant;Bedrag excl. btw;Btw;Totaal;Betaald op\n" +
                  $"{year}-0001;{ImportKit.D(jan)};Fietsplein B.V.;1.000,00;210,00;1.210,00;{ImportKit.D(jan.AddDays(10))}\n" +
                  $"{year}-0002;{ImportKit.D(jan.AddDays(1))};Oude Klant;500,00;;605,00;\n" +
                  $"C-1;{ImportKit.D(jan.AddDays(2))};Oude Klant;-100,00;-21,00;-121,00;\n";
        var check = await ImportKit.CheckAsync(c, ImportKit.Body("facturen", csv));
        Assert.True(check.GetProperty("needsChoice").GetBoolean());
        var result = await ImportKit.RunAsync(c, ImportKit.Body("facturen", csv, customers: new Dictionary<string, int> { ["oude klant"] = 0 }));
        Assert.Equal(3, result.GetProperty("created").GetInt32());

        var list = (await c.GetFromJsonAsync<JsonElement>("/api/imported-invoices")).EnumerateArray().ToList();
        Assert.Equal(3, list.Count);
        var second = list.Single(i => i.GetProperty("number").GetString() == $"{year}-0002");
        // Zonder btw-kolom maar met een totaal: de btw is het verschil.
        Assert.Equal(105m, second.GetProperty("vat").GetDecimal());
        Assert.Equal(customer, list.Single(i => i.GetProperty("number").GetString() == $"{year}-0001").GetProperty("customerId").GetInt32());

        var after = await c.GetFromJsonAsync<JsonElement>($"/api/tax/annual?year={year}");
        Assert.Equal(before.GetProperty("revenue").GetDecimal() + 1400m, after.GetProperty("revenue").GetDecimal());
        Assert.Equal(3, after.GetProperty("importedInvoices").GetInt32());
        var janRevenue = after.GetProperty("months").EnumerateArray().First().GetProperty("revenue").GetDecimal();
        Assert.Equal(1400m, janRevenue - before.GetProperty("months").EnumerateArray().First().GetProperty("revenue").GetDecimal());
        var csvExport = await c.GetStringAsync($"/api/tax/annual.csv?year={year}");
        Assert.Contains("Geïmporteerde facturen", csvExport);
        Assert.Contains($"{year}-0002;{ImportKit.D(jan.AddDays(1))};Oude Klant;500,00;105,00;605,00;", csvExport);
        var insights = await c.GetFromJsonAsync<JsonElement>($"/api/insights?period=eigen&from={year}-01-01&to={year}-01-31");
        Assert.Equal(1400m, insights.GetProperty("revenue").GetDecimal());

        // Op het dashboard telt alleen de betaalde factuur mee, met btw, net als je eigen betaalde facturen.
        Assert.Equal(dashBefore + 1210m, (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("revenueYear").GetDecimal());

        // Het eigen nummer en de btw-aangifte veranderen niet.
        Assert.Equal(nextBefore, (await c.GetFromJsonAsync<JsonElement>($"/api/invoices/next-number?date={jan:yyyy-MM-dd}")).GetProperty("number").GetString());
        var vatAfter = await c.GetFromJsonAsync<JsonElement>($"/api/tax/vat-return?year={year}&quarter=1");
        Assert.Equal(vatBefore.GetProperty("total").GetDecimal(), vatAfter.GetProperty("total").GetDecimal());
        Assert.Equal(vatBefore.GetProperty("revenue").GetDecimal(), vatAfter.GetProperty("revenue").GetDecimal());

        // Alleen bekijken: er is geen manier om een geïmporteerde factuur te wijzigen of te verwijderen, en de klant blijft staan.
        var id = second.GetProperty("id").GetInt32();
        Assert.True((await c.PutAsJsonAsync($"/api/imported-invoices/{id}", new { })).StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);
        Assert.True((await c.DeleteAsync($"/api/imported-invoices/{id}")).StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound);
        var oude = (await ImportKit.CustomersAsync(c)).Single(x => x.GetProperty("name").GetString() == "Oude Klant").GetProperty("id").GetInt32();
        var delete = await c.DeleteAsync($"/api/customers/{oude}");
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Contains("geïmporteerde facturen", await delete.ErrorAsync());

        // Een nummer dat al als eigen factuur bestaat, slaan we altijd over; een eerder geïmporteerd nummer kan bijgewerkt worden.
        var own = await InvoiceKit.InvoiceAsync(c, customer, "verzonden", 1, 100);
        var ownNumber = (await InvoiceKit.GetInvoiceAsync(c, own)).GetProperty("number").GetString();
        var dupCsv = "Factuurnummer;Factuurdatum;Klant;Bedrag excl. btw;Btw\n" +
                     $"{ownNumber};{ImportKit.D(jan)};Fietsplein;5,00;0\n{year}-0002;{ImportKit.D(jan.AddDays(1))};Oude Klant;600,00;126,00\n";
        var dup = await ImportKit.CheckAsync(c, ImportKit.Body("facturen", dupCsv, duplicates: "bijwerken"));
        var notes = ImportKit.Messages(dup, "duplicates");
        Assert.Contains(notes, n => n.StartsWith($"2: Factuur {ownNumber} staat al als gewone factuur in Potel"));
        Assert.Contains(notes, n => n.StartsWith($"3: Factuur {year}-0002 is al eerder geïmporteerd. We werken hem bij"));
        var upd = await ImportKit.RunAsync(c, ImportKit.Body("facturen", dupCsv, duplicates: "bijwerken"));
        Assert.Equal(1, upd.GetProperty("updated").GetInt32());
        Assert.Equal(1, upd.GetProperty("skipped").GetInt32());
        var updated = (await c.GetFromJsonAsync<JsonElement>("/api/imported-invoices")).EnumerateArray().Single(i => i.GetProperty("number").GetString() == $"{year}-0002");
        Assert.Equal(600m, updated.GetProperty("subtotal").GetDecimal());
    }

    [Fact]
    public async Task Undo_removes_exactly_what_the_import_created_except_what_is_used_since()
    {
        var c = await InvoiceKit.ReadyAsync(f, "imp-undo");
        var mine = await CreateCustomerAsync(c, "Al bestaand");
        var mineProject = await InvoiceKit.ProjectAsync(c, mine, 90, "Eigen project");
        await InvoiceKit.TimeAsync(c, mineProject, 60, InvoiceKit.Day(Day));

        var csv = "Client,Project,Description,Billable,Start date,Duration,User\n" +
                  $"Gebruikt,Alpha,Een,Yes,{Day:yyyy-MM-dd},01:00:00,Ann\n" +
                  $"Ongebruikt,Beta,Twee,Yes,{Day:yyyy-MM-dd},02:00:00,Ann\n" +
                  $"Al bestaand,Eigen project,Drie,Yes,{Day:yyyy-MM-dd},00:30:00,Ann\n";
        var batch = await ImportKit.RunAsync(c, ImportKit.Body("uren", csv,
            customers: new Dictionary<string, int> { ["gebruikt"] = 0, ["ongebruikt"] = 0 },
            projects: new Dictionary<string, int> { ["gebruikt\nalpha"] = 0, ["ongebruikt\nbeta"] = 0 }));
        Assert.Equal(3, batch.GetProperty("created").GetInt32());
        var batchId = batch.GetProperty("id").GetInt32();

        // Sindsdien gebruikt: een factuur voor de klant Gebruikt.
        var gebruikt = (await ImportKit.CustomersAsync(c)).Single(x => x.GetProperty("name").GetString() == "Gebruikt").GetProperty("id").GetInt32();
        await InvoiceKit.InvoiceAsync(c, gebruikt, "concept", 1, 50);

        var undo = await c.PostAsync($"/api/imports/{batchId}/undo", null);
        Assert.True(undo.StatusCode == HttpStatusCode.OK, await undo.Content.ReadAsStringAsync());
        var body = await undo.JsonAsync();
        // Weg: drie boekingen, de projecten Alpha en Beta en de klant Ongebruikt. De klant met een factuur blijft staan.
        Assert.Equal(6, body.GetProperty("removed").GetInt32());
        Assert.Equal(1, body.GetProperty("kept").GetInt32());
        var names = (await ImportKit.CustomersAsync(c)).Select(x => x.GetProperty("name").GetString()).ToList();
        Assert.Contains("Gebruikt", names);
        Assert.Contains("Al bestaand", names);
        Assert.DoesNotContain("Ongebruikt", names);
        var projects = (await ImportKit.ProjectsAsync(c)).Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Eigen project"], projects);
        // De boeking die er al was, staat er nog; de geïmporteerde boeking op hetzelfde project niet.
        var left = await ImportKit.EntriesAsync(c, Day, Day);
        Assert.Single(left);
        Assert.Equal(60, left[0].GetProperty("minutes").GetInt32());

        var twice = await c.PostAsync($"/api/imports/{batchId}/undo", null);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        var list = (await c.GetFromJsonAsync<JsonElement>("/api/imports")).EnumerateArray().ToList();
        Assert.False(list.Single(b => b.GetProperty("id").GetInt32() == batchId).GetProperty("canUndo").GetBoolean());
    }

    [Fact]
    public async Task Undo_keeps_invoiced_hours_and_ends_after_24_hours()
    {
        var c = await InvoiceKit.ReadyAsync(f, "imp-24u");
        var customer = await CreateCustomerAsync(c, "Klant 24");
        var project = await InvoiceKit.ProjectAsync(c, customer, 100, "P24");
        var csv = $"Client,Project,Description,Billable,Start date,Duration,User\nKlant 24,P24,Gefactureerd,Yes,{Day:yyyy-MM-dd},01:00:00,A\nKlant 24,P24,Niet factureerbaar,No,{Day.AddDays(1):yyyy-MM-dd},02:00:00,A\n";
        var batch = await ImportKit.RunAsync(c, ImportKit.Body("uren", csv));
        var invoice = await c.PostAsJsonAsync($"/api/customers/{customer}/invoice-hours", new { });
        Assert.Equal(HttpStatusCode.Created, invoice.StatusCode);

        var old = await ImportKit.RunAsync(c, ImportKit.Body("klanten", "Naam\nOude import\n"));
        f.Clock.Value = f.Clock.Value.AddHours(25);
        try
        {
            var late = await c.PostAsync($"/api/imports/{old.GetProperty("id").GetInt32()}/undo", null);
            Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
            Assert.Contains("24 uur", await late.ErrorAsync());
        }
        finally
        {
            f.Clock.Value = f.Clock.Value.AddHours(-25);
        }

        var undo = await (await c.PostAsync($"/api/imports/{batch.GetProperty("id").GetInt32()}/undo", null)).JsonAsync();
        Assert.Equal(1, undo.GetProperty("removed").GetInt32());
        Assert.Equal(1, undo.GetProperty("kept").GetInt32());
        Assert.Contains(undo.GetProperty("keptReasons").EnumerateArray(), r => r.GetString()!.Contains("op een factuur"));
        var entries = await ImportKit.EntriesAsync(c, Day, Day.AddDays(1));
        Assert.Equal("Gefactureerd", Assert.Single(entries).GetProperty("description").GetString());
        Assert.Contains(await ImportKit.ProjectsAsync(c), p => p.GetProperty("id").GetInt32() == project);
    }

    [Fact]
    public async Task Imports_stay_inside_their_own_workspace()
    {
        var a = await RegisterAsync(f, Mail("imp-iso-a"));
        var b = await RegisterAsync(f, Mail("imp-iso-b"));
        var foreignCustomer = await CreateCustomerAsync(b, "Klant van B");
        var foreignProject = await InvoiceKit.ProjectAsync(b, foreignCustomer, 50, "Project van B");

        // Een id uit een andere werkruimte kiezen werkt niet: dat is voor deze werkruimte een klant die niet bestaat.
        var csv = $"Factuurnummer;Datum;Klant;Bedrag\nX-1;{ImportKit.D(Day)};Klant van B;100\n";
        var check = await ImportKit.CheckAsync(a, ImportKit.Body("facturen", csv, customers: new Dictionary<string, int> { ["klant van b"] = foreignCustomer }));
        Assert.False(check.GetProperty("ok").GetBoolean());
        Assert.Contains(ImportKit.Messages(check), e => e.Contains("bestaat niet (meer)"));
        Assert.Equal(HttpStatusCode.BadRequest, (await a.PostAsJsonAsync("/api/imports", ImportKit.Body("facturen", csv, customers: new Dictionary<string, int> { ["klant van b"] = foreignCustomer }))).StatusCode);
        // De naam van een klant in een andere werkruimte geeft ook geen match.
        Assert.Equal(JsonValueKind.Null, check.GetProperty("customers")[0].GetProperty("match").ValueKind);
        Assert.DoesNotContain(check.GetProperty("customerOptions").EnumerateArray(), o => o.GetProperty("id").GetInt32() == foreignCustomer);

        var hours = $"Client,Project,Start date,Duration\nKlant van B,Project van B,{Day:yyyy-MM-dd},1:00\n";
        var hourCheck = await ImportKit.CheckAsync(a, ImportKit.Body("uren", hours, projects: new Dictionary<string, int> { ["klant van b\nproject van b"] = foreignProject }));
        Assert.Contains(ImportKit.Messages(hourCheck), e => e.Contains("bestaat niet (meer)"));

        var batch = await ImportKit.RunAsync(a, ImportKit.Body("facturen", csv, customers: new Dictionary<string, int> { ["klant van b"] = 0 }));
        var id = batch.GetProperty("id").GetInt32();
        // B ziet de import en de factuur niet, en kan hem niet ongedaan maken.
        Assert.Empty((await b.GetFromJsonAsync<JsonElement>("/api/imports")).EnumerateArray());
        Assert.Empty((await b.GetFromJsonAsync<JsonElement>("/api/imported-invoices")).EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/imports/{id}/undo", null)).StatusCode);
        Assert.Single(await ImportKit.CustomersAsync(b));
        Assert.Single((await a.GetFromJsonAsync<JsonElement>("/api/imported-invoices")).EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsync($"/api/imports/{id}/undo", null)).StatusCode);
        Assert.Single(await ImportKit.CustomersAsync(b));
    }

    [Fact]
    public async Task Only_an_admin_can_import()
    {
        var admin = await RegisterAsync(f, Mail("imp-rol"));
        var email = Mail("imp-rol-mw");
        await CreateUserAsync(admin, email);
        var employee = await LoginAsync(f, email);
        var body = ImportKit.Body("klanten", "Naam\nIemand\n");
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync("/api/imports/preview", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync("/api/imports/check", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsJsonAsync("/api/imports", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.GetAsync("/api/imports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PostAsync("/api/imports/1/undo", null)).StatusCode);
        Assert.Empty(await ImportKit.CustomersAsync(admin));
        // Geïmporteerde facturen bekijken mag wie ook facturen ziet.
        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync("/api/imported-invoices")).StatusCode);
    }

    [Fact]
    public async Task Files_that_are_too_big_too_long_or_not_csv_are_refused()
    {
        var c = await RegisterAsync(f, Mail("imp-grens"));
        async Task<string> Refused(object body)
        {
            var res = await c.PostAsJsonAsync("/api/imports/preview", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return await res.ErrorAsync();
        }
        Assert.Equal(ImportFile.TooMany, await Refused(ImportKit.Body("klanten", "Naam\n" + string.Concat(Enumerable.Range(0, 10_001).Select(i => $"Klant {i}\n")))));
        var big = new byte[ImportFile.MaxBytes + 1];
        Array.Fill(big, (byte)'a');
        Assert.Equal(ImportFile.TooBig, await Refused(new { kind = "klanten", data = Convert.ToBase64String(big) }));
        Assert.Equal(ImportFile.NotCsv, await Refused(new { kind = "klanten", data = Convert.ToBase64String([0x50, 0x4B, 0x03, 0x04, 0x00, 0x01]) }));
        Assert.Equal(ImportFile.NotCsv, await Refused(new { kind = "klanten", data = "geen base64!" }));
        Assert.Equal(Importer.UnknownKind, await Refused(new { kind = "leads", data = ImportKit.Data("Naam\nx\n") }));
        Assert.Equal(ImportFile.NoRows, await Refused(ImportKit.Body("klanten", "Naam;E-mail\n;\n")));

        // Een verplicht veld zonder kolom, of een kolom die niet in het bestand staat.
        var noName = await c.PostAsJsonAsync("/api/imports/check", ImportKit.Body("klanten", "Naam;E-mail\nA;a@b.nl\n", mapping: new Dictionary<string, string?> { ["email"] = "E-mail" }));
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode);
        Assert.Contains("verplicht", await noName.ErrorAsync());
        var wrong = await c.PostAsJsonAsync("/api/imports/check", ImportKit.Body("klanten", "Naam;E-mail\nA;a@b.nl\n", mapping: new Dictionary<string, string?> { ["name"] = "Bestaat niet" }));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("staat niet in dit bestand", await wrong.ErrorAsync());
    }

    [Fact]
    public async Task Workspace_export_and_delete_include_imports()
    {
        var c = await RegisterAsync(f, Mail("imp-export"));
        await ImportKit.RunAsync(c, ImportKit.Body("facturen", $"Nummer;Datum;Klant;Bedrag\nOUD-1;{ImportKit.D(Day)};Oud;10\n", customers: new Dictionary<string, int> { ["oud"] = 0 }));
        var export = await c.GetStringAsync("/api/workspace/export");
        Assert.Contains("\"importBatches\"", export);
        Assert.Contains("OUD-1", export);
        var id = await WorkspaceIdAsync(c);
        var res = await c.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        Assert.False(await db.ImportedInvoices.IgnoreQueryFilters().AnyAsync(i => i.WorkspaceId == id));
        Assert.False(await db.ImportBatches.IgnoreQueryFilters().AnyAsync(b => b.WorkspaceId == id));
    }

    [Theory]
    [InlineData("01:30:00", 90)]
    [InlineData("1:30", 90)]
    [InlineData("26:15:00", 1575)]
    [InlineData("00:20:31", 21)]
    [InlineData("1,5", 90)]
    [InlineData("1.25", 75)]
    [InlineData("0.75", 45)]
    [InlineData("1h 30m", 90)]
    [InlineData("45 min", 45)]
    [InlineData("1:75", null)]
    [InlineData("anderhalf", null)]
    [InlineData("", null)]
    public void Durations_from_time_trackers(string text, int? minutes) => Assert.Equal(minutes, ImportFile.Minutes(text));

    [Fact]
    public void Text_never_starts_like_a_formula_and_dates_follow_the_file()
    {
        Assert.Equal("SOM(A1)", ImportFile.Text("=SOM(A1)", 100));
        Assert.Equal("cmd", ImportFile.Text("+-@=cmd", 100));
        Assert.Equal("bugfix", ImportFile.Text("- bugfix", 100));
        Assert.Null(ImportFile.Text(" = ", 100));
        Assert.Equal("abc", ImportFile.Text("abcdef", 3));
        Assert.Equal("0031 20 1234567", ImportFile.Phone("+31 20 1234567", 50));

        Assert.True(ImportFile.MonthFirst(["10/25/2025", "01/02/2025"]));
        Assert.False(ImportFile.MonthFirst(["25/10/2025", "01/02/2025"]));
        Assert.False(ImportFile.MonthFirst(["01/02/2025"]));
        Assert.Equal(new DateTime(2025, 2, 1), ImportFile.Date("01/02/2025", false));
        Assert.Equal(new DateTime(2025, 1, 2), ImportFile.Date("01/02/2025", true));
        Assert.Equal(new DateTime(2025, 10, 25), ImportFile.Date("2025-10-25 09:00:00", false));
        Assert.Equal(new DateTime(2025, 3, 4), ImportFile.Date("4-3-25", false));
        Assert.Null(ImportFile.Date("31-02-2025", false));

        Assert.Equal((true, (bool?)true), ImportFile.Flag("Yes"));
        Assert.Equal((true, (bool?)false), ImportFile.Flag("nee"));
        Assert.Equal((true, (bool?)null), ImportFile.Flag(" "));
        Assert.False(ImportFile.Flag("misschien").Ok);
    }
}

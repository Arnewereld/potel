using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Kilometerregistratie: ritten vastleggen, retourrit, vaste routes, totalen per maand en jaar, doorbelasten op een factuur
// (met dezelfde bescherming tegen dubbel factureren als uren) en de aftrek op het jaaroverzicht.
public class TripTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    public static object Trip(string date = "2026-10-01", decimal? km = 45, string kind = "zakelijk", string? purpose = "Klantbezoek",
        int? customerId = null, int? projectId = null, bool billable = false, int? start = null, int? end = null, string from = "Amsterdam", string to = "Utrecht") =>
        new { date, from, to, kilometres = km, purpose, kind, customerId, projectId, odometerStart = start, odometerEnd = end, billable };

    public static async Task<JsonElement> CreateAsync(HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/api/trips", body);
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return await res.JsonAsync();
    }

    public static async Task<int> RatedCustomerAsync(HttpClient c, string name, decimal? rate)
    {
        var res = await c.PostAsJsonAsync("/api/customers", new { name, company = $"{name} B.V.", address = "Dorpsstraat 1", city = "1234 AB Utrecht", kilometreRate = rate });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    public static async Task<int> RatedProjectAsync(HttpClient c, int customerId, decimal? rate, string name = "Project", string billing = "uur")
    {
        var res = await c.PostAsJsonAsync("/api/projects", new { name, customerId, status = "actief", billing, hourlyRate = 95, fixedPrice = 1000, color = "#000000", kilometreRate = rate });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    static int Id(JsonElement e) => e.GetProperty("id").GetInt32();

    static IEnumerable<JsonElement> KmLines(JsonElement invoice) =>
        invoice.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("unit").GetString() == "km");

    static async Task<JsonElement> YearAsync(HttpClient c, int year) => await c.GetFromJsonAsync<JsonElement>($"/api/trips?year={year}");

    static async Task<JsonElement?> InvoiceOfTripAsync(HttpClient c, int tripId)
    {
        var t = await c.GetFromJsonAsync<JsonElement>($"/api/trips/{tripId}");
        return t.GetProperty("invoiceId").ValueKind == JsonValueKind.Null ? null : t;
    }

    [Fact]
    public async Task A_trip_is_validated_and_takes_its_kilometres_from_the_odometer()
    {
        var c = await ReadyAsync(factory, "rit-invoer");
        async Task<string> Rejected(object body)
        {
            var res = await c.PostAsJsonAsync("/api/trips", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return await res.ErrorAsync();
        }

        // De kilometerstand gaat voor wat er bij kilometers staat.
        var t = await CreateAsync(c, Trip(km: 10, start: 48210, end: 48255));
        Assert.Equal(45m, t.GetProperty("kilometres").GetDecimal());
        Assert.Equal(48210, t.GetProperty("odometerStart").GetInt32());
        Assert.Equal("Zakelijk, eigen auto", t.GetProperty("kindLabel").GetString());
        // Op een tiende kilometer, half van nul af.
        Assert.Equal(12.4m, (await CreateAsync(c, Trip(km: 12.35m))).GetProperty("kilometres").GetDecimal());

        Assert.Contains("hoger", await Rejected(Trip(start: 500, end: 500)));
        Assert.Contains("kilometers", await Rejected(Trip(km: 0)));
        Assert.Contains("10.000", await Rejected(Trip(km: 10_001)));
        Assert.Contains("doel", await Rejected(Trip(purpose: " ")));
        Assert.Contains("vandaan", await Rejected(Trip(from: "")));
        Assert.Contains("wat voor rit", await Rejected(Trip(kind: "fiets")));
        Assert.Contains("klant of project", await Rejected(Trip(billable: true)));
        Assert.Contains("privérit", await Rejected(Trip(kind: "prive", billable: true)));
        // Een privérit heeft geen doel nodig en hangt nooit aan een klant.
        var customerId = await CustomerAsync(c);
        var priv = await CreateAsync(c, Trip(kind: "prive", purpose: null, customerId: customerId));
        Assert.Equal(JsonValueKind.Null, priv.GetProperty("customerId").ValueKind);

        // Een project bepaalt de klant; een project van een andere klant kan niet.
        var other = await CustomerAsync(c, "Ander");
        var projectId = await RatedProjectAsync(c, customerId, null);
        Assert.Equal(customerId, (await CreateAsync(c, Trip(projectId: projectId))).GetProperty("customerId").GetInt32());
        Assert.Contains("andere klant", await Rejected(Trip(projectId: projectId, customerId: other)));
    }

    [Fact]
    public async Task Return_trip_swaps_the_addresses_and_continues_the_odometer()
    {
        var c = await ReadyAsync(factory, "rit-retour");
        var customerId = await RatedCustomerAsync(c, "Retour", 0.3m);
        var t = await CreateAsync(c, Trip(start: 1000, end: 1045, customerId: customerId, billable: true, from: "Keizersgracht 100, Amsterdam", to: "Oudegracht 12, Utrecht"));
        var res = await c.PostAsync($"/api/trips/{Id(t)}/return", null);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var back = await res.JsonAsync();
        Assert.Equal("Oudegracht 12, Utrecht", back.GetProperty("from").GetString());
        Assert.Equal("Keizersgracht 100, Amsterdam", back.GetProperty("to").GetString());
        Assert.Equal(45m, back.GetProperty("kilometres").GetDecimal());
        Assert.Equal(1045, back.GetProperty("odometerStart").GetInt32());
        Assert.Equal(1090, back.GetProperty("odometerEnd").GetInt32());
        Assert.True(back.GetProperty("billable").GetBoolean());
        Assert.Equal(customerId, back.GetProperty("customerId").GetInt32());
        Assert.Equal(t.GetProperty("date").GetString(), back.GetProperty("date").GetString());
    }

    [Fact]
    public async Task Routes_can_be_saved_picked_and_removed()
    {
        var c = await ReadyAsync(factory, "rit-route");
        var customerId = await CustomerAsync(c);
        var res = await c.PostAsJsonAsync("/api/trips/routes", new { from = "Amsterdam", to = "Utrecht", kilometres = 45, purpose = "Overleg", kind = "zakelijk", customerId, billable = true });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var route = await res.JsonAsync();
        Assert.Equal("Amsterdam naar Utrecht", route.GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/trips/routes", new { name = "Leeg", from = "A", to = "B", kilometres = 0 })).StatusCode);
        var routes = await c.GetFromJsonAsync<List<JsonElement>>("/api/trips/routes");
        Assert.Equal(Id(route), Assert.Single(routes!).GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/trips/routes/{Id(route)}")).StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<List<JsonElement>>("/api/trips/routes"))!);
    }

    [Fact]
    public async Task The_year_shows_totals_per_month_and_kind_and_the_deduction()
    {
        var c = await ReadyAsync(factory, "rit-jaar");
        await CreateAsync(c, Trip(date: "2026-01-15", km: 100));
        await CreateAsync(c, Trip(date: "2026-01-20", km: 33.3m));
        await CreateAsync(c, Trip(date: "2026-03-02", km: 50, kind: "auto-van-de-zaak"));
        await CreateAsync(c, Trip(date: "2026-03-05", km: 20, kind: "prive", purpose: null));
        await CreateAsync(c, Trip(date: "2025-12-31", km: 999));

        var y = await YearAsync(c, 2026);
        Assert.Equal(4, y.GetProperty("count").GetInt32());
        Assert.Equal(133.3m, y.GetProperty("businessKm").GetDecimal());
        Assert.Equal(50m, y.GetProperty("companyCarKm").GetDecimal());
        Assert.Equal(20m, y.GetProperty("privateKm").GetDecimal());
        Assert.Equal(0.25m, y.GetProperty("rate").GetDecimal());
        Assert.Equal(33.33m, y.GetProperty("deduction").GetDecimal());   // 133,3 × 0,25 = 33,325
        var months = y.GetProperty("months");
        Assert.Equal(12, months.GetArrayLength());
        Assert.Equal(133.3m, months[0].GetProperty("businessKm").GetDecimal());
        Assert.Equal(2, months[0].GetProperty("trips").GetInt32());
        Assert.Equal(50m, months[2].GetProperty("companyCarKm").GetDecimal());
        Assert.Equal(20m, months[2].GetProperty("privateKm").GetDecimal());

        var last = await YearAsync(c, 2025);
        Assert.Equal(0.23m, last.GetProperty("rate").GetDecimal());
        Assert.Equal(229.77m, last.GetProperty("deduction").GetDecimal());

        // Voor 2027 staat het bedrag nog niet vast: geen aftrek, wel een melding.
        await CreateAsync(c, Trip(date: "2027-01-04", km: 10));
        var next = await YearAsync(c, 2027);
        Assert.Equal(JsonValueKind.Null, next.GetProperty("rate").ValueKind);
        Assert.Equal(0m, next.GetProperty("deduction").GetDecimal());
        Assert.Contains("2027", next.GetProperty("note").GetString());
    }

    [Fact]
    public async Task Another_workspace_never_sees_or_links_trips_customers_or_projects()
    {
        var a = await ReadyAsync(factory, "rit-a");
        var b = await ReadyAsync(factory, "rit-b");
        var customerA = await RatedCustomerAsync(a, "Van A", 0.3m);
        var projectA = await RatedProjectAsync(a, customerA, 0.4m);
        var tripA = await CreateAsync(a, Trip(projectId: projectA, billable: true));
        var routeA = await (await a.PostAsJsonAsync("/api/trips/routes", new { from = "A", to = "B", kilometres = 5, kind = "zakelijk" })).JsonAsync();

        Assert.Equal(0, (await YearAsync(b, 2026)).GetProperty("count").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/api/trips/{Id(tripA)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PutAsJsonAsync($"/api/trips/{Id(tripA)}", Trip())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/trips/{Id(tripA)}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/trips/{Id(tripA)}/return", null)).StatusCode);
        Assert.Empty((await b.GetFromJsonAsync<List<JsonElement>>("/api/trips/routes"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/trips/routes/{Id(routeA)}")).StatusCode);

        // Een klant of project van een ander kan niet aan een rit of route van jou hangen.
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/trips", Trip(customerId: customerA))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/trips", Trip(projectId: projectA))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/trips/routes", new { from = "A", to = "B", kilometres = 5, kind = "zakelijk", projectId = projectA })).StatusCode);
        var own = await CreateAsync(b, Trip());
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PutAsJsonAsync($"/api/trips/{Id(own)}", Trip(customerId: customerA))).StatusCode);

        // De open ritten van A komen nooit op een factuur van B, ook niet met hun id.
        var customerB = await RatedCustomerAsync(b, "Van B", 0.3m);
        var invoiceB = await InvoiceAsync(b, customerB, "concept", 1, 100);
        Assert.Equal(0, (await b.GetFromJsonAsync<JsonElement>($"/api/trips/billable?customerId={customerA}")).GetProperty("trips").GetInt32());
        Assert.Equal(HttpStatusCode.Conflict, (await b.PostAsJsonAsync($"/api/invoices/{invoiceB}/trips", new { tripIds = new[] { Id(tripA) } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync($"/api/invoices/{invoiceB}/trips", new { })).StatusCode);
        Assert.Null(await InvoiceOfTripAsync(a, Id(tripA)));
        // En andersom: A kan geen ritten op een factuur van B zetten.
        Assert.Equal(HttpStatusCode.NotFound, (await a.PostAsJsonAsync($"/api/invoices/{invoiceB}/trips", new { })).StatusCode);
    }

    [Fact]
    public async Task Staff_change_only_their_own_trips_and_a_read_only_workspace_cannot_add_any()
    {
        var admin = await ReadyAsync(factory, "rit-rollen");
        var email = $"rit-medewerker-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(admin, email);
        var staff = await TestApi.LoginAsync(factory, email);

        var mine = await CreateAsync(staff, Trip(km: 10));
        Assert.True(mine.GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/trips/{Id(mine)}", Trip(km: 12))).StatusCode);
        var boss = await CreateAsync(admin, Trip(km: 20));
        // De medewerker ziet de rit van de beheerder wel, maar kan hem niet aanpassen of verwijderen.
        var seen = (await YearAsync(staff, 2026)).GetProperty("trips").EnumerateArray().Single(t => Id(t) == Id(boss));
        Assert.False(seen.GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.PutAsJsonAsync($"/api/trips/{Id(boss)}", Trip(km: 1))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.DeleteAsync($"/api/trips/{Id(boss)}")).StatusCode);
        // Een beheerder kan alle ritten aanpassen.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/trips/{Id(mine)}", Trip(km: 14))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/trips/{Id(mine)}")).StatusCode);

        await TestApi.ExpireTrialAsync(factory, await TestApi.WorkspaceIdAsync(admin));
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.PostAsJsonAsync("/api/trips", Trip())).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.PostAsync($"/api/trips/{Id(boss)}/return", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/trips?year=2026")).StatusCode);
    }

    [Fact]
    public async Task Kilometre_rates_are_checked_and_rounded()
    {
        var c = await ReadyAsync(factory, "rit-tarief");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/customers", new { name = "Negatief", kilometreRate = -0.1m })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/customers", new { name = "Te hoog", kilometreRate = 10.01m })).StatusCode);
        var customerId = await RatedCustomerAsync(c, "Afgerond", 0.345m);
        var customer = await c.GetFromJsonAsync<JsonElement>($"/api/customers/{customerId}");
        Assert.Equal(0.35m, customer.GetProperty("kilometreRate").GetDecimal());
        var projectId = await RatedProjectAsync(c, customerId, 0.4m);
        Assert.Equal(0.4m, (await c.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}")).GetProperty("kilometreRate").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsJsonAsync("/api/projects", new { name = "Fout", customerId, status = "actief", billing = "uur", hourlyRate = 95, color = "#000000", kilometreRate = 11 })).StatusCode);
    }

    [Fact]
    public async Task Billable_trips_go_on_an_invoice_per_rate_and_lock_like_hours()
    {
        var c = await ReadyAsync(factory, "rit-factuur");
        var customerId = await RatedCustomerAsync(c, "Fietsplein", 0.3m);
        var cheap = await RatedProjectAsync(c, customerId, null, "Klanttarief");
        var dear = await RatedProjectAsync(c, customerId, 0.4m, "Eigen tarief");
        var t1 = await CreateAsync(c, Trip(date: "2026-10-01", km: 45, projectId: cheap, billable: true));
        var t2 = await CreateAsync(c, Trip(date: "2026-10-02", km: 45, customerId: customerId, billable: true));
        var t3 = await CreateAsync(c, Trip(date: "2026-10-03", km: 10, projectId: dear, billable: true));
        await CreateAsync(c, Trip(date: "2026-10-04", km: 99, customerId: customerId));                                // niet doorbelasten
        await CreateAsync(c, Trip(date: "2026-10-05", km: 7, kind: "prive", purpose: null));                          // privé
        var noRate = await RatedCustomerAsync(c, "Zonder tarief", null);
        await CreateAsync(c, Trip(km: 12, customerId: noRate, billable: true));

        var open = await c.GetFromJsonAsync<JsonElement>($"/api/trips/billable?customerId={customerId}");
        Assert.Equal(3, open.GetProperty("trips").GetInt32());
        Assert.Equal(100m, open.GetProperty("kilometres").GetDecimal());
        Assert.Equal(31m, open.GetProperty("amount").GetDecimal());       // 90 × 0,30 + 10 × 0,40
        var none = await c.GetFromJsonAsync<JsonElement>($"/api/trips/billable?customerId={noRate}");
        Assert.Equal(0, none.GetProperty("trips").GetInt32());
        Assert.Equal(1, none.GetProperty("withoutRate").GetInt32());
        Assert.Equal(12m, none.GetProperty("withoutRateKilometres").GetDecimal());

        var invoiceId = await InvoiceAsync(c, customerId, "concept", 1, 100);
        var res = await c.PostAsJsonAsync($"/api/invoices/{invoiceId}/trips", new { });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var inv = await res.JsonAsync();
        var lines = KmLines(inv).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("Reiskosten, 90 km à € 0,30", lines[0].GetProperty("description").GetString());
        Assert.Equal(90m, lines[0].GetProperty("quantity").GetDecimal());
        Assert.Equal(0.3m, lines[0].GetProperty("unitPrice").GetDecimal());
        Assert.Equal(21m, lines[0].GetProperty("vatRate").GetDecimal());
        Assert.Equal("Reiskosten, 10 km à € 0,40", lines[1].GetProperty("description").GetString());
        Assert.Equal("2026-10-01T00:00:00", inv.GetProperty("deliveryFrom").GetString());
        Assert.Equal("2026-10-03T00:00:00", inv.GetProperty("deliveryTo").GetString());

        // Gefactureerde ritten liggen vast en komen niet nog een keer op een factuur.
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsJsonAsync($"/api/trips/{Id(t1)}", Trip())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/trips/{Id(t2)}")).StatusCode);
        Assert.Contains("conceptfactuur", await (await c.DeleteAsync($"/api/trips/{Id(t3)}")).ErrorAsync());
        var other = await InvoiceAsync(c, customerId, "concept", 1, 100);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/invoices/{other}/trips", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/invoices/{other}/trips", new { tripIds = new[] { Id(t1) } })).StatusCode);
        Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>($"/api/trips/billable?customerId={customerId}")).GetProperty("trips").GetInt32());
        // Met ritten erop kan de klant van de factuur niet meer veranderen.
        var another = await RatedCustomerAsync(c, "Ander", 0.3m);
        var moved = WithLines(inv, inv.GetProperty("lines"));
        var json = JsonSerializer.SerializeToNode(moved)!;
        json["customerId"] = another;
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/invoices/{invoiceId}", json)).StatusCode);

        // De regel met het klanttarief eraf: die ritten komen weer vrij, de rit met het projecttarief blijft vast.
        var keep = inv.GetProperty("lines").EnumerateArray().Where(l => l.GetProperty("description").GetString() != "Reiskosten, 90 km à € 0,30").ToList();
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/invoices/{invoiceId}", WithLines(inv, keep))).StatusCode);
        Assert.Null(await InvoiceOfTripAsync(c, Id(t1)));
        Assert.Null(await InvoiceOfTripAsync(c, Id(t2)));
        Assert.NotNull(await InvoiceOfTripAsync(c, Id(t3)));

        // De factuur verwijderen geeft de laatste rit ook weer vrij.
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/invoices/{invoiceId}")).StatusCode);
        Assert.Null(await InvoiceOfTripAsync(c, Id(t3)));
        Assert.Equal(3, (await c.GetFromJsonAsync<JsonElement>($"/api/trips/billable?customerId={customerId}")).GetProperty("trips").GetInt32());

        // Alleen gekozen ritten, en niet op een verstuurde factuur of creditnota.
        var picked = await c.PostAsJsonAsync($"/api/invoices/{other}/trips", new { tripIds = new[] { Id(t3) } });
        Assert.Equal(HttpStatusCode.OK, picked.StatusCode);
        Assert.Equal(10m, KmLines(await picked.JsonAsync()).Sum(l => l.GetProperty("quantity").GetDecimal()));
        var sent = await InvoiceAsync(c, customerId, "verzonden", 1, 100);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/invoices/{sent}/trips", new { })).StatusCode);
    }

    [Fact]
    public async Task A_fully_credited_invoice_frees_its_trips()
    {
        var c = await ReadyAsync(factory, "rit-credit");
        var customerId = await RatedCustomerAsync(c, "Credit", 0.3m);
        var trip = await CreateAsync(c, Trip(km: 100, customerId: customerId, billable: true));
        var invoiceId = await InvoiceAsync(c, customerId, "concept", 1, 100);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/invoices/{invoiceId}/trips", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, invoiceId, "verzonden")).StatusCode);
        Assert.Contains("staat op factuur", await (await c.DeleteAsync($"/api/trips/{Id(trip)}")).ErrorAsync());
        var credit = (await Json(await c.PostAsync($"/api/invoices/{invoiceId}/credit", null))).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, credit, "verzonden")).StatusCode);
        Assert.Null(await InvoiceOfTripAsync(c, Id(trip)));
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/invoices/{credit}/trips", new { })).StatusCode);
    }

    [Fact]
    public async Task Invoicing_a_project_takes_its_trips_only_when_asked()
    {
        var c = await ReadyAsync(factory, "rit-project");
        var customerId = await RatedCustomerAsync(c, "Project", 0.3m);
        var projectId = await RatedProjectAsync(c, customerId, null);
        await TimeAsync(c, projectId, 120, "2026-10-02");
        await CreateAsync(c, Trip(date: "2026-09-28", km: 40, projectId: projectId, billable: true));
        await CreateAsync(c, Trip(km: 40, customerId: customerId, billable: true));   // van de klant, niet van het project

        var withoutTrips = await Json(await c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false }));
        Assert.Empty(KmLines(await GetInvoiceAsync(c, Id(withoutTrips))));
        Assert.Equal(1, (await c.GetFromJsonAsync<JsonElement>($"/api/trips/billable?projectId={projectId}")).GetProperty("trips").GetInt32());

        // Alleen nog ritten open: dan kan de factuur er ook alleen uit ritten bestaan.
        var res = await c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { detailed = false, trips = true });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var inv = await GetInvoiceAsync(c, Id(await res.JsonAsync()));
        Assert.Equal("Reiskosten, 40 km à € 0,30", Assert.Single(inv.GetProperty("lines").EnumerateArray()).GetProperty("description").GetString());
        Assert.Equal("2026-09-28T00:00:00", inv.GetProperty("deliveryFrom").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/projects/{projectId}/invoice", new { trips = true })).StatusCode);
        // De rit van de klant zelf staat nog open.
        Assert.Equal(1, (await c.GetFromJsonAsync<JsonElement>($"/api/trips/billable?customerId={customerId}")).GetProperty("trips").GetInt32());
    }

    [Fact]
    public async Task Removing_demo_data_removes_demo_trips_but_keeps_your_own()
    {
        var c = await TestApi.RegisterAsync(factory, $"rit-demo-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/workspace/demo-data", null)).StatusCode);
        var today = new BusinessClock().Today;
        var demo = (await YearAsync(c, today.Year)).GetProperty("trips").EnumerateArray().ToList();
        Assert.NotEmpty(demo);
        Assert.All(demo, t => Assert.True(t.GetProperty("demo").GetBoolean()));
        // De doorbelaste voorbeeldritten van vorige maanden staan op hun voorbeeldfactuur, met een eigen regel.
        var billed = demo.Where(t => t.GetProperty("invoiceId").ValueKind != JsonValueKind.Null).ToList();
        Assert.NotEmpty(billed);
        var invoice = await GetInvoiceAsync(c, billed[0].GetProperty("invoiceId").GetInt32());
        var line = Assert.Single(KmLines(invoice));
        Assert.Equal(demo.Where(t => t.GetProperty("invoiceId").ValueKind != JsonValueKind.Null && t.GetProperty("invoiceId").GetInt32() == Id(invoice))
            .Sum(t => t.GetProperty("kilometres").GetDecimal()), line.GetProperty("quantity").GetDecimal());
        Assert.NotEmpty((await c.GetFromJsonAsync<List<JsonElement>>("/api/trips/routes"))!);
        // Voorbeeldritten tellen niet mee voor de aftrek.
        Assert.Equal(0m, (await YearAsync(c, today.Year)).GetProperty("deduction").GetDecimal());

        var project = (await c.GetFromJsonAsync<List<JsonElement>>("/api/projects"))!.First(p => p.GetProperty("status").GetString() == "afgerond");
        var own = await CreateAsync(c, Trip(projectId: Id(project)));
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/workspace/demo-data")).StatusCode);
        var left = (await YearAsync(c, 2026)).GetProperty("trips").EnumerateArray().ToList();
        Assert.Equal(new[] { Id(own) }, left.Select(Id));
        Assert.Empty((await c.GetFromJsonAsync<List<JsonElement>>("/api/trips/routes"))!);
        // Het voorbeeldproject met een eigen rit blijft staan.
        Assert.Contains((await c.GetFromJsonAsync<List<JsonElement>>("/api/projects"))!, p => Id(p) == Id(project));
    }

    [Fact]
    public async Task The_export_contains_trips_and_routes()
    {
        var c = await ReadyAsync(factory, "rit-export");
        var trip = await CreateAsync(c, Trip(km: 17));
        await c.PostAsJsonAsync("/api/trips/routes", new { from = "A", to = "B", kilometres = 5, kind = "zakelijk" });
        var export = await c.GetFromJsonAsync<JsonElement>("/api/workspace/export");
        Assert.Equal(Id(trip), Assert.Single(export.GetProperty("trips").EnumerateArray()).GetProperty("id").GetInt32());
        Assert.Single(export.GetProperty("tripRoutes").EnumerateArray());
    }
}

// De aftrek voor zakelijke kilometers met je eigen auto op het jaaroverzicht en in de CSV. De klok staat op 20 mei 2026.
public class TripAnnualTests(MayInAmsterdamFactory factory) : IClassFixture<MayInAmsterdamFactory>
{
    void Edit(int workspaceId, Action<AppDb> change)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        db.Tenant.WorkspaceId = workspaceId;
        change(db);
        db.SaveChanges();
    }

    [Fact]
    public async Task Business_kilometres_with_your_own_car_lower_the_result_and_show_in_the_csv()
    {
        var c = await ReadyAsync(factory, "rit-jaaroverzicht");
        var ws = await TestApi.WorkspaceIdAsync(c);
        var customerId = await TripTests.RatedCustomerAsync(c, "Klant", 0.3m);
        await InvoiceAsync(c, customerId, "verzonden", 10, 100, issueDate: "2026-05-20");                         // 1.000 omzet
        await TripTests.CreateAsync(c, TripTests.Trip(date: "2026-01-10", km: 33.3m));
        await TripTests.CreateAsync(c, TripTests.Trip(date: "2026-02-10", km: 33.3m));
        await TripTests.CreateAsync(c, TripTests.Trip(date: "2026-03-10", km: 33.3m, customerId: customerId, billable: true));
        await TripTests.CreateAsync(c, TripTests.Trip(date: "2026-03-11", km: 500, kind: "auto-van-de-zaak"));
        await TripTests.CreateAsync(c, TripTests.Trip(date: "2026-04-01", km: 80, kind: "prive", purpose: null));
        await TripTests.CreateAsync(c, TripTests.Trip(date: "2025-12-31", km: 100));
        var demo = await TripTests.CreateAsync(c, TripTests.Trip(date: "2026-04-02", km: 1000));
        Edit(ws, db => db.Trips.Single(t => t.Id == demo.GetProperty("id").GetInt32()).IsDemo = true);

        var y = await c.GetFromJsonAsync<JsonElement>("/api/tax/annual?year=2026");
        var m = y.GetProperty("mileage");
        Assert.Equal(5, m.GetProperty("trips").GetInt32());
        Assert.Equal(99.9m, m.GetProperty("businessKm").GetDecimal());
        Assert.Equal(500m, m.GetProperty("companyCarKm").GetDecimal());
        Assert.Equal(80m, m.GetProperty("privateKm").GetDecimal());
        Assert.Equal(0.25m, m.GetProperty("rate").GetDecimal());
        Assert.Equal(24.98m, m.GetProperty("deduction").GetDecimal());       // 99,9 × 0,25 = 24,975
        Assert.Equal(24.98m, y.GetProperty("costs").GetDecimal());
        Assert.Equal(975.02m, y.GetProperty("result").GetDecimal());
        // Per maand zo afgerond dat de maanden samen de aftrek zijn: 8,33 + 8,32 + 8,33.
        var months = y.GetProperty("months").EnumerateArray().Select(x => x.GetProperty("costs").GetDecimal()).ToList();
        Assert.Equal(new[] { 8.33m, 8.32m, 8.33m }, months.Take(3));
        Assert.Equal(24.98m, months.Sum());
        Assert.Contains("zakelijk-gebruik-privevervoermiddel-2026", m.GetProperty("source").GetString());

        var res = await c.GetAsync("/api/tax/annual.csv?year=2026");
        var csv = Encoding.UTF8.GetString(await res.Content.ReadAsByteArrayAsync());
        Assert.Contains("Kosten zonder btw;24,98\r\n", csv);
        Assert.Contains("Zakelijke kilometers eigen auto;99,90\r\n", csv);
        Assert.Contains("Kilometeraftrek eigen auto (0,25 per km);24,98\r\n", csv);
        Assert.Contains("Zakelijke kilometers auto van de zaak;500,00\r\n", csv);
        Assert.Contains("Privékilometers;80,00\r\n", csv);
        Assert.Contains("Resultaat voor belasting;975,02\r\n", csv);
        Assert.Contains("\r\nRitten\r\n", csv);
        Assert.Contains("10-03-2026;Amsterdam;Utrecht;Klantbezoek;Zakelijk, eigen auto;33,30;;;Klant B.V.;;\r\n", csv);
        Assert.DoesNotContain("02-04-2026", csv);
        Assert.DoesNotContain("31-12-2025", csv);

        // 2025: € 0,23 per kilometer.
        var last = (await c.GetFromJsonAsync<JsonElement>("/api/tax/annual?year=2025")).GetProperty("mileage");
        Assert.Equal(23m, last.GetProperty("deduction").GetDecimal());
    }
}

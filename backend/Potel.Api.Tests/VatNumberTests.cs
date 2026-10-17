using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Potel.Api.Data;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// De vorm van btw-nummers per EU-land (alleen de vorm, zonder VIES) en het voorstel voor de btw-regeling bij een klant
// in het buitenland.
public partial class VatNumberRuleTests
{
    [Fact]
    public void The_example_of_every_eu_country_has_the_right_form_and_every_eu_country_has_one()
    {
        Assert.Equal(Countries.Eu.Order(), VatNumbers.Formats.Keys.Order());
        foreach (var (country, (code, _, example)) in VatNumbers.Formats)
        {
            Assert.True(VatNumbers.LooksValid(example, country), $"{country}: {example}");
            Assert.StartsWith(code, example);
            Assert.Null(VatNumbers.Problem(example, country));
            // Met spaties, punten en kleine letters is het hetzelfde nummer.
            var messy = $" {example[..2].ToLowerInvariant()} {string.Join('.', example[2..].Chunk(3).Select(c => new string(c)))} ";
            Assert.Equal(example, VatNumbers.Clean(messy, country));
            Assert.True(VatNumbers.LooksValid(messy, country), $"{country}: {messy}");
        }
    }

    [Theory]
    [InlineData("Duitsland", "DE12345678")]
    [InlineData("Duitsland", "DE1234567890")]
    [InlineData("Nederland", "NL123456789B1")]
    [InlineData("Nederland", "NL123456789X01")]
    [InlineData("België", "BE2123456789")]
    [InlineData("Oostenrijk", "ATU1234567")]
    [InlineData("Frankrijk", "FRIO123456789")]
    [InlineData("Ierland", "IE12345678")]
    [InlineData("Spanje", "ES123456789")]
    [InlineData("Zweden", "SE123456789012")]
    [InlineData("Roemenië", "RO0123456")]
    [InlineData("Duitsland", "DE12345678A")]
    public void Numbers_with_the_wrong_form_are_refused(string country, string number)
    {
        Assert.False(VatNumbers.LooksValid(number, country));
        var problem = VatNumbers.Problem(number, country);
        Assert.NotNull(problem);
        Assert.Contains(country, problem);
        Assert.Contains(VatNumbers.Formats[country].Example, problem);
        Assert.DoesNotContain("—", problem);
    }

    [Theory]
    [InlineData("123456789", "Duitsland", "DE123456789")]
    [InlineData("de-123-456-789", "Germany", "DE123456789")]
    [InlineData("GR 123 456 789", "Griekenland", "EL123456789")]
    [InlineData("u12345678", "Oostenrijk", "ATU12345678")]
    [InlineData("B12345678", "Spanje", "ESB12345678")]
    [InlineData("0123.456.789", "België", "BE0123456789")]
    [InlineData("812345678B01", "", "NL812345678B01")]
    [InlineData("GB 123 456 789", "Verenigd Koninkrijk", "GB123456789")]
    [InlineData("CHE-123.456.789 MWST", "Zwitserland", "CHE123456789MWST")]
    public void Numbers_are_cleaned_and_get_the_country_code_of_the_customer(string input, string country, string clean)
    {
        Assert.Equal(clean, VatNumbers.Clean(input, country));
    }

    [Fact]
    public void A_number_from_another_eu_country_is_named_and_outside_the_eu_nothing_is_checked()
    {
        var problem = VatNumbers.Problem("NL123456789B01", "Duitsland")!;
        Assert.Contains("hoort dus bij Nederland", problem);
        Assert.Contains("begint met DE", problem);
        Assert.Null(VatNumbers.Problem("GB123456789", "Verenigd Koninkrijk"));
        Assert.Null(VatNumbers.Problem("anything goes", "Verenigde Staten"));
        Assert.Null(VatNumbers.Problem(null, "Duitsland"));
        Assert.Null(VatNumbers.Problem("  ", "Duitsland"));
        Assert.False(VatNumbers.LooksValid("GB123456789", "Verenigd Koninkrijk"));
    }

    [Fact]
    public void A_very_long_number_is_refused_quickly()
    {
        var hostile = "DE" + new string('1', 1_000_000);
        var watch = Stopwatch.StartNew();
        Assert.False(VatNumbers.LooksValid(hostile, "Duitsland"));
        Assert.NotNull(VatNumbers.Problem(hostile, "Duitsland"));
        Assert.True(VatNumbers.Clean(hostile, "Duitsland")!.Length <= VatNumbers.MaxLength + 1);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void The_suggested_regime_follows_country_and_vat_number()
    {
        Customer C(string country, string? vat = null) => new() { Name = "Klant", Country = country, VatNumber = vat };
        Assert.Null(VatRegimes.Suggest(C("Nederland", "NL812345678B01")));
        Assert.Null(VatRegimes.Suggest(C("", null)));
        Assert.Equal(VatRegimes.ReverseCharge, VatRegimes.Suggest(C("Duitsland", "DE123456789")));
        Assert.Equal(VatRegimes.ReverseCharge, VatRegimes.Suggest(C("Germany", "de 123456789")));
        Assert.Null(VatRegimes.Suggest(C("Duitsland", "DE1234")));
        Assert.Null(VatRegimes.Suggest(C("Duitsland")));
        Assert.Equal(VatRegimes.OutsideEu, VatRegimes.Suggest(C("Verenigde Staten")));
        Assert.Equal(VatRegimes.OutsideEu, VatRegimes.Suggest(C("Verenigd Koninkrijk", "GB123456789")));
        Assert.Null(VatRegimes.Suggest(null));

        var kor = new Settings { VatRegime = VatRegimes.Kor };
        Assert.Equal(VatRegimes.Kor, VatRegimes.DefaultFor(kor, C("Nederland")));
        Assert.Equal(VatRegimes.Kor, VatRegimes.DefaultFor(kor, C("Duitsland", "DE1234")));
        Assert.Equal(VatRegimes.ReverseCharge, VatRegimes.DefaultFor(kor, C("Ierland", "IE6388047V")));
        Assert.Equal(VatRegimes.OutsideEu, VatRegimes.DefaultFor(kor, C("Zwitserland")));
    }

    [GeneratedRegex(@"^\s*'([^']+)':\s*\['([A-Z]{2})',\s*String\.raw`([^`]*)`,\s*'([A-Z0-9]+)'\],\s*$", RegexOptions.Multiline)]
    private static partial Regex FrontendFormat();

    [Fact]
    public void The_frontend_checks_the_same_forms()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "frontend", "src", "lib", "vat.ts"))) dir = dir.Parent;
        if (dir is null) return; // alleen de backend uitgecheckt
        var source = File.ReadAllText(Path.Combine(dir.FullName, "frontend", "src", "lib", "vat.ts"));
        var frontend = FrontendFormat().Matches(source).ToDictionary(m => m.Groups[1].Value, m => (m.Groups[2].Value, m.Groups[3].Value, m.Groups[4].Value));
        Assert.Equal(VatNumbers.Formats.Keys.Order(), frontend.Keys.Order());
        foreach (var (country, format) in VatNumbers.Formats) Assert.Equal(format, frontend[country]);
    }
}

// Btw-nummers bij het opslaan van een klant, en btw verlegd alleen met een nummer van de juiste vorm. Een verstuurde
// factuur verandert nooit, ook niet als het nummer van de klant van voor deze controle is.
public class VatNumberTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    static object Customer(string country, string? vatNumber, string name = "Kunde") =>
        new { name, company = $"{name} GmbH", address = "Hauptstraße 1", city = "10115 Berlin", country, vatNumber };

    static object Invoice(int customerId, string vatRegime, string status = "concept") => new
    {
        customerId, issueDate = "2026-10-01", dueDate = "2026-10-15", deliveryFrom = "2026-09-30", status, vatRegime,
        lines = new[] { new { description = "Werk", quantity = 1, unit = "stuk", unitPrice = 100, vatRate = vatRegime == "normaal" ? 21 : 0 } },
    };

    [Fact]
    public async Task Customers_are_saved_with_a_clean_number_and_a_wrong_form_is_refused()
    {
        var c = await ReadyAsync(factory, "btwnummer");
        var res = await c.PostAsJsonAsync("/api/customers", Customer("Duitsland", "de 123.456.789"));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await res.JsonAsync()).GetProperty("id").GetInt32();
        Assert.Equal("DE123456789", (await c.GetFromJsonAsync<JsonElement>($"/api/customers/{id}")).GetProperty("vatNumber").GetString());

        var wrong = await c.PostAsJsonAsync("/api/customers", Customer("Duitsland", "DE12345"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("DE123456789", await wrong.ErrorAsync());
        var other = await c.PostAsJsonAsync("/api/customers", Customer("Germany", "NL123456789B01"));
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
        Assert.Contains("hoort dus bij Nederland", await other.ErrorAsync());
        // Buiten de EU geen controle; leeg mag altijd.
        var british = await c.PostAsJsonAsync("/api/customers", Customer("Verenigd Koninkrijk", "GB 123 456 789"));
        Assert.Equal(HttpStatusCode.Created, british.StatusCode);
        Assert.Equal("GB123456789", (await british.JsonAsync()).GetProperty("vatNumber").GetString());
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/customers", Customer("Duitsland", null))).StatusCode);

        // Bijwerken met een verkeerd nummer kan niet en laat de klant zoals hij was.
        var put = await c.PutAsJsonAsync($"/api/customers/{id}", Customer("Duitsland", "DE1"));
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Equal("DE123456789", (await c.GetFromJsonAsync<JsonElement>($"/api/customers/{id}")).GetProperty("vatNumber").GetString());
        // Zonder landcode zet Potel die van de klant ervoor.
        Assert.Equal(HttpStatusCode.OK, (await c.PutAsJsonAsync($"/api/customers/{id}", Customer("Oostenrijk", "U12345678"))).StatusCode);
        Assert.Equal("ATU12345678", (await c.GetFromJsonAsync<JsonElement>($"/api/customers/{id}")).GetProperty("vatNumber").GetString());

        // Een andere werkruimte kan de klant niet bijwerken, een medewerker wel, en alleen-lezen niemand.
        var stranger = await ReadyAsync(factory, "btwnummer-ander");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.PutAsJsonAsync($"/api/customers/{id}", Customer("Duitsland", "DE999999999"))).StatusCode);
        var employee = $"btw-medewerker-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(c, employee);
        var staff = await TestApi.LoginAsync(factory, employee);
        Assert.Equal(HttpStatusCode.BadRequest, (await staff.PutAsJsonAsync($"/api/customers/{id}", Customer("Oostenrijk", "ATU1"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await staff.PutAsJsonAsync($"/api/customers/{id}", Customer("Oostenrijk", "ATU87654321"))).StatusCode);
        await TestApi.ExpireTrialAsync(factory, await TestApi.WorkspaceIdAsync(c));
        Assert.Equal(HttpStatusCode.PaymentRequired, (await c.PutAsJsonAsync($"/api/customers/{id}", Customer("Oostenrijk", "ATU12345678"))).StatusCode);
        Assert.Equal("ATU87654321", (await c.GetFromJsonAsync<JsonElement>($"/api/customers/{id}")).GetProperty("vatNumber").GetString());
    }

    [Fact]
    public async Task Reverse_charge_needs_a_number_of_the_right_form_but_a_sent_invoice_never_changes()
    {
        var c = await ReadyAsync(factory, "btwnummer-verlegd");
        var ws = await TestApi.WorkspaceIdAsync(c);
        var customer = (await (await c.PostAsJsonAsync("/api/customers", Customer("Duitsland", "DE123456789"))).JsonAsync()).GetProperty("id").GetInt32();
        var sent = (await (await c.PostAsJsonAsync("/api/invoices", Invoice(customer, "verlegd", "verzonden"))).JsonAsync()).GetProperty("id").GetInt32();
        var draft = (await (await c.PostAsJsonAsync("/api/invoices", Invoice(customer, "verlegd"))).JsonAsync()).GetProperty("id").GetInt32();
        // De factuur zelf, zonder de klant zoals die nu is (die stuurt de server voor het scherm mee).
        async Task<string> Frozen(int id)
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(await c.GetStringAsync($"/api/invoices/{id}"))!.AsObject();
            json.Remove("customer");
            return json.ToJsonString();
        }
        var before = await Frozen(sent);

        // Een nummer van voor deze controle, met een verkeerde vorm.
        WorkflowKit.InWorkspace(factory, ws, db => { db.Customers.Single(x => x.Id == customer).VatNumber = "DE12"; return db.SaveChanges(); });

        var refused = await c.PostAsJsonAsync("/api/invoices", Invoice(customer, "verlegd"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("heeft niet de vorm van een btw-nummer uit Duitsland", await refused.ErrorAsync());
        var send = await SetStatusAsync(c, draft, "verzonden");
        Assert.Equal(HttpStatusCode.BadRequest, send.StatusCode);
        Assert.Contains("DE12", await send.ErrorAsync());

        // De verstuurde factuur blijft precies zoals hij was, en corrigeren met een creditnota kan nog steeds.
        Assert.Equal(before, await Frozen(sent));
        Assert.Equal("DE123456789", (await GetInvoiceAsync(c, sent)).GetProperty("buyer").GetProperty("vatNumber").GetString());
        var credit = await c.PostAsync($"/api/invoices/{sent}/credit", null);
        Assert.Equal(HttpStatusCode.Created, credit.StatusCode);
        var creditId = (await credit.JsonAsync()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, creditId, "verzonden")).StatusCode);
        Assert.Equal("DE123456789", (await GetInvoiceAsync(c, creditId)).GetProperty("buyer").GetProperty("vatNumber").GetString());

        // Een nieuwe factuur uit uren krijgt dan niet vanzelf btw verlegd, maar de standaard van de werkruimte.
        var project = await ProjectAsync(c, customer, 95);
        await TimeAsync(c, project, 60, "2026-10-01");
        var fromHours = (await (await c.PostAsJsonAsync($"/api/projects/{project}/invoice", new { detailed = false })).JsonAsync()).GetProperty("id").GetInt32();
        Assert.Equal("normaal", (await GetInvoiceAsync(c, fromHours)).GetProperty("vatRegime").GetString());

        // Met een goed nummer wel.
        (await c.PutAsJsonAsync($"/api/customers/{customer}", Customer("Duitsland", "DE987654321"))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(c, draft, "verzonden")).StatusCode);
        var other = await ProjectAsync(c, customer, 95, "Tweede");
        await TimeAsync(c, other, 60, "2026-10-02");
        var next = (await (await c.PostAsJsonAsync($"/api/projects/{other}/invoice", new { detailed = false })).JsonAsync()).GetProperty("id").GetInt32();
        Assert.Equal("verlegd", (await GetInvoiceAsync(c, next)).GetProperty("vatRegime").GetString());
        // En op de eerder verstuurde factuur staat nog steeds het nummer van toen.
        Assert.Equal("DE123456789", (await GetInvoiceAsync(c, sent)).GetProperty("buyer").GetProperty("vatNumber").GetString());
    }
}

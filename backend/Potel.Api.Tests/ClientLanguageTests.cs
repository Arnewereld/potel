using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Invoicing;
using Potel.Api.Payments;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// Het woordenboek van de server: elke sleutel in beide talen, met dezelfde woorden tussen accolades.
public partial class ClientTextsTests
{
    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    static string[] Placeholders(string text) => Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

    [Fact]
    public void Every_key_exists_in_both_languages_with_the_same_placeholders()
    {
        Assert.Equal(ClientTexts.Dutch.Keys.Order(), ClientTexts.English.Keys.Order());
        foreach (var (key, dutch) in ClientTexts.Dutch)
        {
            var english = ClientTexts.English[key];
            Assert.False(string.IsNullOrWhiteSpace(dutch), key);
            Assert.False(string.IsNullOrWhiteSpace(english), key);
            Assert.True(Placeholders(dutch).SequenceEqual(Placeholders(english)), $"{key}: {string.Join(",", Placeholders(dutch))} tegen {string.Join(",", Placeholders(english))}");
            Assert.DoesNotContain("—", english);
        }
    }

    [Fact]
    public void English_amounts_dates_and_numbers_use_english_notation()
    {
        Assert.Equal("€1,234.56", ClientTexts.Euro("en", 1234.56m));
        Assert.Equal("-€1,234.56", ClientTexts.Euro("en", -1234.56m));
        Assert.Equal("€0.00", ClientTexts.Euro("en", 0));
        Assert.Equal(1234.56m.ToString("C", System.Globalization.CultureInfo.GetCultureInfo("nl-NL")), ClientTexts.Euro("nl", 1234.56m));
        Assert.Equal("14 October 2026", ClientTexts.Date("en", new DateTime(2026, 10, 14)));
        Assert.Equal("14 oktober 2026", ClientTexts.Date("nl", new DateTime(2026, 10, 14)));
        Assert.Equal("1,234.5", ClientTexts.Number("en", 1234.5m));
        Assert.Equal("1.234,5", ClientTexts.Number("nl", 1234.5m));
        Assert.Equal("October", ClientTexts.Month("en", 10));
        Assert.Equal("oktober", ClientTexts.Month("nl", 10));
    }

    [Fact]
    public void Filling_in_happens_once_and_unknown_languages_are_dutch()
    {
        // Een naam van een klant met accolades wordt niet nog eens ingevuld.
        Assert.Equal("Please quote invoice number {name} with your payment.", ClientTexts.T("en", "mail.pay.reference", ("number", "{name}")));
        Assert.Equal("Vermeld bij je betaling het factuurnummer {number}.", ClientTexts.T("fr", "mail.pay.reference"));
        Assert.Equal(Languages.Dutch, Languages.Of("fr"));
        Assert.Equal(Languages.English, Languages.Of(" EN "));
        Assert.Equal(Languages.Dutch, Languages.Of(null));
        Assert.True(Languages.IsKnown(null));
        Assert.True(Languages.IsKnown("En"));
        Assert.False(Languages.IsKnown("fr"));
    }

    [Fact]
    public void Recurring_periods_are_named_in_the_language_of_the_customer()
    {
        Assert.Equal("October 2026", RecurringPeriods.Label(new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), "en"));
        Assert.Equal("oktober 2026", RecurringPeriods.Label(new DateTime(2026, 10, 1), new DateTime(2026, 10, 31)));
        Assert.Equal("February to April 2026", RecurringPeriods.Label(new DateTime(2026, 2, 1), new DateTime(2026, 4, 30), "en"));
        Assert.Equal("15 October to 14 November 2026", RecurringPeriods.Label(new DateTime(2026, 10, 15), new DateTime(2026, 11, 14), "en"));
        Assert.Equal("Hosting October 2026 (Q4)", RecurringPeriods.Fill("Hosting {Maand} {jaar} ({kwartaal})", new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), "en"));
        Assert.Equal("Hosting Oktober 2026", RecurringPeriods.Fill("Hosting {Maand} {jaar}", new DateTime(2026, 10, 1), new DateTime(2026, 10, 31)));
    }

    [Fact]
    public void The_english_final_notice_still_names_the_statutory_costs()
    {
        var business = MailTemplates.Consequences(consumer: false, 3000m, new DateTime(2026, 11, 1), "en");
        Assert.Contains("statutory commercial interest", business);
        Assert.Contains("€425.00", business);
        var consumer = MailTemplates.Consequences(consumer: true, 120m, new DateTime(2026, 11, 1), "en");
        Assert.Contains("within 14 days", consumer);
        Assert.Contains("16 November 2026", consumer);
        Assert.Contains("€40.00", consumer);
        // Eigen teksten bij Instellingen zijn Nederlands; een Engelse klant krijgt de Engelse standaardtekst.
        var custom = new ReminderSettings { FirstSubject = "Even een seintje over {nummer}" };
        Assert.Equal("Even een seintje over {nummer}", MailTemplates.For(custom, MailSteps.First).Subject);
        Assert.Equal("Reminder: invoice {nummer}", MailTemplates.For(custom, MailSteps.First, language: "en").Subject);
        Assert.Equal("Credit note {nummer} from {bedrijf}", MailTemplates.For(custom, MailSteps.Invoice, credit: true, language: "en").Subject);
    }
}

// Het woordenboek van de frontend (frontend/src/lib/docTexts.ts): elke sleutel in beide talen, net als op de server.
public partial class DocTextsTests
{
    [GeneratedRegex(@"^\s*'([a-zA-Z0-9.]+)':\s*'([^']*)',\s*$", RegexOptions.Multiline)]
    private static partial Regex Entry();

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex Placeholder();

    static string? Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "frontend", "src", "lib", "docTexts.ts"))) dir = dir.Parent;
        return dir is null ? null : File.ReadAllText(Path.Combine(dir.FullName, "frontend", "src", "lib", "docTexts.ts"));
    }

    static Dictionary<string, string> Block(string source, string start)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"{start} niet gevonden in docTexts.ts");
        var to = source.IndexOf("\n}", from, StringComparison.Ordinal);
        var block = source[from..to];
        var entries = Entry().Matches(block).Select(m => (Key: m.Groups[1].Value, Text: m.Groups[2].Value)).ToList();
        // Elke regel met een sleutel moet het parseren halen, anders mist de test er een.
        Assert.Equal(Regex.Matches(block, @"^\s*'[a-zA-Z0-9.]+':", RegexOptions.Multiline).Count, entries.Count);
        Assert.Equal(entries.Count, entries.Select(e => e.Key).Distinct().Count());
        return entries.ToDictionary(e => e.Key, e => e.Text);
    }

    static string[] Placeholders(string text) => Placeholder().Matches(text).Select(m => m.Groups[1].Value).Distinct().Order().ToArray();

    [Fact]
    public void Every_key_exists_in_both_languages_with_the_same_placeholders()
    {
        if (Source() is not { } source) return; // alleen de backend uitgecheckt
        var nl = Block(source, "const nl = {");
        var en = Block(source, "const en: Record<DocKey, string> = {");
        Assert.True(nl.Count > 100, "docTexts.ts lijkt leeg");
        Assert.Equal(nl.Keys.Order(), en.Keys.Order());
        foreach (var (key, dutch) in nl)
        {
            Assert.False(string.IsNullOrWhiteSpace(dutch), key);
            Assert.False(string.IsNullOrWhiteSpace(en[key]), key);
            Assert.True(Placeholders(dutch).SequenceEqual(Placeholders(en[key])), key);
            Assert.DoesNotContain("—", en[key]);
        }
        // De standaardopmerking op een factuur is op de server en in de frontend dezelfde.
        Assert.Equal(ClientTexts.Dutch["note.default"], nl["note.default"]);
        Assert.Equal(ClientTexts.English["note.default"], en["note.default"]);
    }

    [Fact]
    public void The_legal_vat_notes_exist_in_both_languages()
    {
        if (Source() is not { } source) return;
        var nl = Block(source, "const nl = {");
        var en = Block(source, "const en: Record<DocKey, string> = {");
        // Verlegd (art. 226 lid 11a btw-richtlijn): "Reverse charge"; beide btw-nummers staan op de factuur.
        Assert.Equal("Btw verlegd naar de afnemer.", nl["invoice.noteReverse"]);
        Assert.StartsWith("Reverse charge", en["invoice.noteReverse"]);
        Assert.Contains("VAT to be accounted for by the recipient", en["invoice.noteReverse"]);
        Assert.Contains("{number}", en["invoice.noteSupplierVat"]);
        Assert.Contains("{number}", en["invoice.noteCustomerVat"]);
        Assert.Equal("VAT reverse charged", en["paper.vatReversed"]);
        // Vrijstelling, KOR en buiten de EU noemen hun grond.
        Assert.Contains("article 11", en["invoice.noteExempt"]);
        Assert.Contains("kleineondernemersregeling", en["invoice.noteKor"]);
        Assert.Contains("outside the EU", en["invoice.noteOutsideEu"]);
    }
}

public class LanguageFactory : QuoteFactory
{
    public readonly FakeMollie Mollie = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:InvoicePayPerMinute", "10000");
        builder.UseSetting("RateLimit:PortalPerMinute", "10000");
        builder.ConfigureTestServices(s => s.AddHttpClient<MollieClient>().ConfigurePrimaryHttpMessageHandler(() => Mollie));
    }
}

// Facturen, offertes en mails in de taal van de klant: de taal komt van de klant (of lead), kan in een concept nog
// veranderen en ligt vast na versturen. Alles wat de klant ziet volgt die taal; Nederlands blijft zoals het was.
public class LanguageTests(LanguageFactory factory) : IClassFixture<LanguageFactory>
{
    static readonly Regex InvoiceToken = new(@"https://potel\.test/factuur#token=([A-Za-z0-9_-]+)");

    record Setup(HttpClient Client, int Workspace, int English, int Dutch, string EnglishEmail);

    async Task<Setup> ReadyAsync(string prefix)
    {
        factory.Clock.SetDay(2026, 10, 12);
        var c = await InvoiceKit.ReadyAsync(factory, prefix);
        var email = $"aoife-{Guid.NewGuid():N}@example.com";
        var res = await c.PostAsJsonAsync("/api/customers", new
        {
            name = "Aoife Murphy", company = "Northwind Labs Ltd", email, address = "12 Grand Canal Quay", city = "Dublin 2", country = "Ierland",
            vatNumber = "IE6388047V", language = "en",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var english = (await res.JsonAsync()).GetProperty("id").GetInt32();
        var dutch = await CustomerAsync(c, "Bakkerij");
        return new Setup(c, await TestApi.WorkspaceIdAsync(c), english, dutch, email);
    }

    static Task<HttpResponseMessage> PostAsync(HttpClient c, int customerId, string status = "concept", string? language = null, string vatRegime = "verlegd")
    {
        var lines = new[] { new { description = "Development work", quantity = 10m, unit = "uur", unitPrice = 100m, vatRate = vatRegime == "normaal" ? 21m : 0m } };
        object body = language is null
            ? new { customerId, issueDate = "2026-10-12", dueDate = "2026-10-26", status, vatRegime, lines }
            : new { customerId, issueDate = "2026-10-12", dueDate = "2026-10-26", status, vatRegime, lines, language };
        return c.PostAsJsonAsync("/api/invoices", body);
    }

    static async Task<int> CreateAsync(HttpClient c, int customerId, string status = "concept", string? language = null, string vatRegime = "verlegd")
    {
        var res = await PostAsync(c, customerId, status, language, vatRegime);
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    static async Task<string?> LanguageAsync(HttpClient c, int invoiceId) => (await GetInvoiceAsync(c, invoiceId)).GetProperty("language").GetString();

    // De factuur terugsturen zoals hij is, met andere waarden voor een paar velden.
    static async Task<HttpResponseMessage> PutAsync(HttpClient c, int id, Action<Dictionary<string, object?>> change)
    {
        var inv = JsonSerializer.Deserialize<Dictionary<string, object?>>(await c.GetStringAsync($"/api/invoices/{id}"))!;
        change(inv);
        return await c.PutAsJsonAsync($"/api/invoices/{id}", inv);
    }

    static object Quote(int? customerId = null, int? leadId = null, string? language = null) => language is null
        ? new
        {
            customerId, leadId, issueDate = "2026-10-12", validUntil = "2026-11-11", vatRegime = customerId is null ? "normaal" : "verlegd", subject = "Payments API",
            lines = new[] { new { description = "API work", quantity = 20, unit = "uur", unitPrice = 100, vatRate = customerId is null ? 21 : 0 } },
        }
        : new
        {
            customerId, leadId, issueDate = "2026-10-12", validUntil = "2026-11-11", vatRegime = "verlegd", subject = "Payments API", language,
            lines = new[] { new { description = "API work", quantity = 20, unit = "uur", unitPrice = 100, vatRate = 0 } },
        };

    static async Task<int> QuoteAsync(HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/api/quotes", body);
        Assert.True(res.StatusCode == HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await res.JsonAsync()).GetProperty("id").GetInt32();
    }

    static async Task<string> QuoteLinkAsync(HttpClient c, int id)
    {
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/quotes/{id}/send", null)).StatusCode);
        var res = await c.PostAsync($"/api/quotes/{id}/link", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.JsonAsync()).GetProperty("path").GetString()!["/offerte#token=".Length..];
    }

    [Fact]
    public async Task New_documents_take_the_language_of_the_customer_and_it_can_change_only_while_draft()
    {
        var s = await ReadyAsync("taal-factuur");
        Assert.Equal("en", (await s.Client.GetFromJsonAsync<JsonElement>($"/api/customers/{s.English}")).GetProperty("language").GetString());
        Assert.Equal("nl", (await s.Client.GetFromJsonAsync<JsonElement>($"/api/customers/{s.Dutch}")).GetProperty("language").GetString());

        var english = await CreateAsync(s.Client, s.English);
        Assert.Equal("en", await LanguageAsync(s.Client, english));
        Assert.Equal("nl", await LanguageAsync(s.Client, await CreateAsync(s.Client, s.Dutch, vatRegime: "normaal")));
        // Wie zelf een taal kiest, krijgt die.
        Assert.Equal("nl", await LanguageAsync(s.Client, await CreateAsync(s.Client, s.English, language: "nl")));
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(s.Client, s.English, language: "fr")).StatusCode);

        // In een concept kan de taal nog veranderen. Een verzoek zonder taal laat hem zoals hij is.
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(s.Client, english, inv => inv["language"] = "nl")).StatusCode);
        Assert.Equal("nl", await LanguageAsync(s.Client, english));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(s.Client, english, inv => inv["language"] = "en")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(s.Client, english, inv => inv.Remove("language"))).StatusCode);
        Assert.Equal("en", await LanguageAsync(s.Client, english));
        Assert.Equal(HttpStatusCode.BadRequest, (await PutAsync(s.Client, english, inv => inv["language"] = "de")).StatusCode);

        // Verstuurd ligt de taal vast, ook als de klant later een andere taal krijgt.
        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(s.Client, english, "verzonden")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PutAsync(s.Client, english, inv => inv["language"] = "nl")).StatusCode);
        var customer = JsonSerializer.Deserialize<Dictionary<string, object?>>(await s.Client.GetStringAsync($"/api/customers/{s.English}"))!;
        customer["language"] = "nl";
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PutAsJsonAsync($"/api/customers/{s.English}", customer)).StatusCode);
        Assert.Equal("en", await LanguageAsync(s.Client, english));
        Assert.Equal("en", WorkflowKit.InWorkspace(factory, s.Workspace, db => db.Invoices.Single(i => i.Id == english).Language));

        // Een creditnota hoort bij de factuur en houdt de taal daarvan, ook als je hem probeert te veranderen.
        var credit = (await (await s.Client.PostAsync($"/api/invoices/{english}/credit", null)).JsonAsync()).GetProperty("id").GetInt32();
        Assert.Equal("en", await LanguageAsync(s.Client, credit));
        Assert.Equal(HttpStatusCode.OK, (await PutAsync(s.Client, credit, inv => inv["language"] = "nl")).StatusCode);
        Assert.Equal("en", await LanguageAsync(s.Client, credit));

        // Een kopie is een nieuwe factuur en krijgt de taal die de klant nu heeft.
        var copy = (await (await s.Client.PostAsync($"/api/invoices/{english}/duplicate", null)).JsonAsync()).GetProperty("id").GetInt32();
        Assert.Equal("nl", await LanguageAsync(s.Client, copy));

        // Een klant met een onbekende taal kan niet.
        customer["language"] = "fr";
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PutAsJsonAsync($"/api/customers/{s.English}", customer)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/customers", new { name = "X", language = "xx" })).StatusCode);
    }

    [Fact]
    public async Task Quotes_take_the_language_of_the_customer_or_the_lead_and_the_lead_passes_it_on()
    {
        var s = await ReadyAsync("taal-offerte");
        var forCustomer = await QuoteAsync(s.Client, Quote(s.English));
        Assert.Equal("en", (await s.Client.GetFromJsonAsync<JsonElement>($"/api/quotes/{forCustomer}")).GetProperty("language").GetString());

        var lead = await s.Client.PostAsJsonAsync("/api/leads", new { name = "John Smith", company = "Contoso Ltd", email = "john@example.com", value = 1000, status = "nieuw", language = "en" });
        Assert.Equal(HttpStatusCode.Created, lead.StatusCode);
        var leadId = (await lead.JsonAsync()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync("/api/leads", new { name = "Fout", status = "nieuw", language = "es" })).StatusCode);
        var forLead = await QuoteAsync(s.Client, Quote(leadId: leadId));
        Assert.Equal("en", (await s.Client.GetFromJsonAsync<JsonElement>($"/api/quotes/{forLead}")).GetProperty("language").GetString());

        // In een concept kun je de taal nog kiezen, na versturen niet meer.
        var put = await s.Client.PutAsJsonAsync($"/api/quotes/{forCustomer}", Quote(s.English, language: "nl"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("nl", (await put.JsonAsync()).GetProperty("language").GetString());
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PutAsJsonAsync($"/api/quotes/{forCustomer}", Quote(s.English, language: "en"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsync($"/api/quotes/{forCustomer}/send", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PutAsJsonAsync($"/api/quotes/{forCustomer}", Quote(s.English, language: "nl"))).StatusCode);

        // Wordt de lead klant, dan neemt de klant de taal mee.
        var converted = await (await s.Client.PostAsync($"/api/leads/{leadId}/convert", null)).JsonAsync();
        Assert.Equal("en", converted.GetProperty("language").GetString());
    }

    [Fact]
    public async Task Mails_to_an_english_customer_are_english_and_dutch_mails_stay_the_same()
    {
        var s = await ReadyAsync("taal-mail");
        var id = await CreateAsync(s.Client, s.English, "verzonden");
        var number = (await GetInvoiceAsync(s.Client, id)).GetProperty("number").GetString()!;

        var preview = await s.Client.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}/mail-preview?step=0");
        Assert.Equal($"Invoice {number} from Mijn Bedrijf", preview.GetProperty("subject").GetString());
        var body = preview.GetProperty("body").GetString()!;
        Assert.StartsWith("Dear Aoife Murphy,", body);
        Assert.Contains($"Here is invoice {number} of 12 October 2026 for €1,000.00.", body);
        Assert.Contains("no later than 26 October 2026?", body);
        Assert.Contains($"Please transfer it to NL00 BANK 0123 4567 89 in the name of Mijn Bedrijf, quoting invoice number {number}.", body);
        Assert.DoesNotContain("Beste", body);

        // De mail die echt weggaat, met de link.
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsJsonAsync($"/api/invoices/{id}/mail", new { step = 0 })).StatusCode);
        var mail = Assert.Single(factory.Mail.To(s.EnglishEmail));
        Assert.Equal($"Invoice {number} from Mijn Bedrijf", mail.Subject);
        Assert.Matches(InvoiceToken, mail.Body);
        Assert.Contains("Kind regards,", mail.Body);

        // Herinneringen en de aanmaning, ook automatisch, in het Engels.
        factory.Clock.SetDay(2026, 11, 5);
        Assert.Equal($"Reminder: invoice {number}", (await s.Client.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}/mail-preview?step=1")).GetProperty("subject").GetString());
        var final = await s.Client.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}/mail-preview?step=3");
        Assert.Equal($"Final notice: invoice {number}", final.GetProperty("subject").GetString());
        Assert.Contains("statutory commercial interest", final.GetProperty("body").GetString());
        Assert.Contains("€150.00", final.GetProperty("body").GetString());
        (await s.Client.PutAsJsonAsync("/api/reminders", new
        {
            enabled = true, firstDays = 7, secondDays = 14, finalDays = 28, first = new { subject = "Even een seintje over {nummer}", body = MailTemplates.First.Body },
        })).EnsureSuccessStatusCode();
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = s.Workspace;
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ReminderService>().RunAsync());
        }
        var reminder = factory.Mail.To(s.EnglishEmail).Last();
        Assert.Equal($"Reminder: invoice {number}", reminder.Subject);
        Assert.Contains("There is still €1,000.00 outstanding.", reminder.Body);

        // Een Nederlandse klant krijgt dezelfde mail als altijd, met de eigen tekst.
        factory.Clock.SetDay(2026, 10, 12);
        var dutch = await CreateAsync(s.Client, s.Dutch, "verzonden", vatRegime: "normaal");
        var dutchNumber = (await GetInvoiceAsync(s.Client, dutch)).GetProperty("number").GetString()!;
        var nl = await s.Client.GetFromJsonAsync<JsonElement>($"/api/invoices/{dutch}/mail-preview?step=0");
        Assert.Equal($"Factuur {dutchNumber} van Mijn Bedrijf", nl.GetProperty("subject").GetString());
        Assert.StartsWith("Beste Bakkerij,", nl.GetProperty("body").GetString());
        Assert.Contains("van 12 oktober 2026 voor € 1.210,00", nl.GetProperty("body").GetString()!.Replace(' ', ' '));
        factory.Clock.SetDay(2026, 11, 5);
        Assert.Equal($"Even een seintje over {dutchNumber}", (await s.Client.GetFromJsonAsync<JsonElement>($"/api/invoices/{dutch}/mail-preview?step=1")).GetProperty("subject").GetString());

        // De offertemail in het Engels.
        factory.Clock.SetDay(2026, 10, 12);
        var quote = await QuoteAsync(s.Client, Quote(s.English));
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsync($"/api/quotes/{quote}/send", null)).StatusCode);
        var quoteMail = await s.Client.GetFromJsonAsync<JsonElement>($"/api/quotes/{quote}/mail-preview");
        Assert.StartsWith("Quote OFF-2026-", quoteMail.GetProperty("subject").GetString());
        Assert.EndsWith(" from Mijn Bedrijf", quoteMail.GetProperty("subject").GetString());
        var quoteBody = quoteMail.GetProperty("body").GetString()!;
        Assert.StartsWith("Hi Aoife,", quoteBody);
        Assert.Contains("for Payments API, for a total of €2,000.00. The quote is valid until 11 November 2026.", quoteBody);
    }

    [Fact]
    public async Task Public_pages_the_portal_and_ideal_follow_the_language_of_the_document()
    {
        var s = await ReadyAsync("taal-openbaar");
        var key = IdealKit.NewKey();
        factory.Mollie.Accounts[key] = ["ideal"];
        (await s.Client.PutAsJsonAsync("/api/settings/mollie", new { key })).EnsureSuccessStatusCode();

        var english = await CreateAsync(s.Client, s.English, "verzonden");
        var dutch = await CreateAsync(s.Client, s.Dutch, "verzonden", vatRegime: "normaal");
        var number = (await GetInvoiceAsync(s.Client, english)).GetProperty("number").GetString()!;
        var dutchNumber = (await GetInvoiceAsync(s.Client, dutch)).GetProperty("number").GetString()!;
        var token = IdealKit.Link(factory, s.Workspace, english);
        var dutchToken = IdealKit.Link(factory, s.Workspace, dutch);

        var page = await IdealKit.PageAsync(factory, token);
        Assert.Equal("en", page.GetProperty("invoice").GetProperty("language").GetString());
        Assert.Equal("Ierland", page.GetProperty("invoice").GetProperty("buyer").GetProperty("country").GetString());
        Assert.Equal("nl", (await IdealKit.PageAsync(factory, dutchToken)).GetProperty("invoice").GetProperty("language").GetString());

        // iDEAL: omschrijving en taal van de betaalpagina van Mollie.
        var checkout = await IdealKit.CheckoutAsync(factory, token);
        var payment = factory.Mollie.Payments[IdealKit.PaymentId(checkout)];
        Assert.Equal($"Invoice {number} from Mijn Bedrijf", payment["description"]!.GetValue<string>());
        Assert.Equal("en_GB", payment["locale"]!.GetValue<string>());
        var status = await (await factory.CreateClient().PostAsJsonAsync("/api/public/invoice/payment-status",
            new { @ref = IdealKit.ReturnRef(factory.Mollie, IdealKit.PaymentId(checkout)) })).JsonAsync();
        Assert.Equal("en", status.GetProperty("language").GetString());
        var dutchPayment = factory.Mollie.Payments[IdealKit.PaymentId(await IdealKit.CheckoutAsync(factory, dutchToken))];
        Assert.Equal($"Factuur {dutchNumber} van Mijn Bedrijf", dutchPayment["description"]!.GetValue<string>());
        Assert.Equal("nl_NL", dutchPayment["locale"]!.GetValue<string>());

        // Zonder iDEAL krijgt de Engelse klant de melding in het Engels.
        var plain = await InvoiceKit.ReadyAsync(factory, "taal-zonder-ideal");
        var plainCustomer = (await (await plain.PostAsJsonAsync("/api/customers", new { name = "Jane Doe", company = "Acme Inc", address = "1 Main Street", city = "Springfield, IL 62701", country = "Verenigde Staten", language = "en" })).JsonAsync()).GetProperty("id").GetInt32();
        var plainInvoice = await CreateAsync(plain, plainCustomer, "verzonden", vatRegime: "buiten-eu");
        var refused = await IdealKit.PayAsync(factory, IdealKit.Link(factory, await TestApi.WorkspaceIdAsync(plain), plainInvoice));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(ClientTexts.English["ideal.notOffered"], await refused.ErrorAsync());

        // Het klantportaal.
        var portal = await s.Client.PostAsJsonAsync($"/api/customers/{s.English}/portal/link", new { expiresInDays = (int?)null });
        Assert.Equal(HttpStatusCode.OK, portal.StatusCode);
        var portalToken = (await portal.JsonAsync()).GetProperty("path").GetString()!["/portaal#token=".Length..];
        var opened = await (await factory.CreateClient().PostAsJsonAsync("/api/public/portal", new { token = portalToken })).JsonAsync();
        Assert.Equal("en", opened.GetProperty("language").GetString());

        // De openbare offerte, met meldingen in het Engels.
        var quote = await QuoteAsync(s.Client, Quote(s.English));
        var quoteToken = await QuoteLinkAsync(s.Client, quote);
        var anonymous = factory.CreateClient();
        var view = await (await anonymous.PostAsJsonAsync("/api/public/quote", new { token = quoteToken })).JsonAsync();
        Assert.Equal("en", view.GetProperty("quote").GetProperty("language").GetString());
        Assert.Equal(ClientTexts.English["quote.nameNeeded"], await (await anonymous.PostAsJsonAsync("/api/public/quote/accept", new { token = quoteToken, name = "A", agree = true })).ErrorAsync());
        Assert.Equal(ClientTexts.English["quote.agreeNeeded"], await (await anonymous.PostAsJsonAsync("/api/public/quote/accept", new { token = quoteToken, name = "Aoife Murphy", agree = false })).ErrorAsync());
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync("/api/public/quote/accept", new { token = quoteToken, name = "Aoife Murphy", agree = true })).StatusCode);
        var decided = await (await anonymous.PostAsJsonAsync("/api/public/quote", new { token = quoteToken })).JsonAsync();
        Assert.StartsWith("This quote was already accepted on ", decided.GetProperty("problem").GetString());
        var again = await anonymous.PostAsJsonAsync("/api/public/quote/reject", new { token = quoteToken });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.StartsWith("This quote was already accepted on ", await again.ErrorAsync());

        // De factuur uit de offerte is Engels, ook de regels en de opmerking die Potel zelf schrijft.
        var fromQuote = (await (await s.Client.PostAsJsonAsync($"/api/quotes/{quote}/invoice", new { deposit = false })).JsonAsync()).GetProperty("id").GetInt32();
        var made = await GetInvoiceAsync(s.Client, fromQuote);
        Assert.Equal("en", made.GetProperty("language").GetString());
        Assert.StartsWith("As per quote OFF-2026-", made.GetProperty("notes").GetString());
        Assert.EndsWith("Thank you for your business!", made.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task Hours_and_their_specification_are_english_for_an_english_customer()
    {
        var s = await ReadyAsync("taal-uren");
        var project = await ProjectAsync(s.Client, s.English, 110, "Payments API");
        async Task Book(string date, int minutes, string description) =>
            Assert.Equal(HttpStatusCode.Created, (await s.Client.PostAsJsonAsync("/api/time", new { projectId = project, date, minutes, description, billable = true })).StatusCode);
        await Book("2026-10-01", 20, "Webhook retries");
        await Book("2026-10-02", 20, "Idempotency keys");
        await Book("2026-10-03", 20, "Load tests");
        var res = await s.Client.PostAsJsonAsync($"/api/projects/{project}/invoice", new { detailed = false });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var id = (await res.JsonAsync()).GetProperty("id").GetInt32();
        var inv = await GetInvoiceAsync(s.Client, id);
        Assert.Equal("en", inv.GetProperty("language").GetString());
        Assert.Equal("Thank you for your business!", inv.GetProperty("notes").GetString());
        Assert.Equal("Payments API: work from 1 Oct to 3 Oct 2026", inv.GetProperty("lines")[0].GetProperty("description").GetString());

        Assert.Equal(HttpStatusCode.OK, (await SetStatusAsync(s.Client, id, "verzonden")).StatusCode);
        var number = (await GetInvoiceAsync(s.Client, id)).GetProperty("number").GetString()!;
        var mine = await s.Client.GetAsync($"/api/invoices/{id}/hours.csv");
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Contains($"timesheet-{number}.csv", mine.Content.Headers.ContentDisposition?.ToString());
        var text = Encoding.UTF8.GetString(await mine.Content.ReadAsByteArrayAsync()).TrimStart('﻿');
        Assert.StartsWith($"Timesheet for invoice {number},Mijn Bedrijf\r\n", text);
        Assert.Contains("Project,Date,Description,Hours\r\n", text);
        Assert.Contains("Payments API,2026-10-01,Webhook retries,0.33", text);
        Assert.Contains("Subtotal Payments API,,,1.00", text);
        Assert.Contains("Total,,,1.00", text);

        // De klant krijgt via de link precies hetzelfde bestand.
        var token = IdealKit.Link(factory, s.Workspace, id);
        var theirs = await factory.CreateClient().PostAsJsonAsync("/api/public/invoice/hours", new { token });
        Assert.Equal(HttpStatusCode.OK, theirs.StatusCode);
        Assert.Contains($"timesheet-{number}.csv", theirs.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal(text, Encoding.UTF8.GetString(await theirs.Content.ReadAsByteArrayAsync()).TrimStart('﻿'));
    }

    [Fact]
    public async Task Another_workspace_cannot_see_or_change_the_language_and_read_only_workspaces_change_nothing()
    {
        var s = await ReadyAsync("taal-eigen");
        var id = await CreateAsync(s.Client, s.English);
        var other = await InvoiceKit.ReadyAsync(factory, "taal-ander");
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/invoices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/invoices/{id}", new { customerId = s.English, language = "nl" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsJsonAsync($"/api/customers/{s.English}", new { name = "Aoife", language = "nl" })).StatusCode);
        // Een offerte of factuur voor een klant uit een andere werkruimte neemt diens taal niet over: die klant bestaat daar niet.
        Assert.Equal(HttpStatusCode.BadRequest, (await other.PostAsJsonAsync("/api/quotes", Quote(s.English))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(other, s.English)).StatusCode);
        Assert.Equal("en", await LanguageAsync(s.Client, id));

        // Een medewerker mag klanten bijwerken, dus ook hun taal.
        var employee = $"taal-medewerker-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(s.Client, employee);
        var staff = await TestApi.LoginAsync(factory, employee);
        var created = await staff.PostAsJsonAsync("/api/customers", new { name = "Hans Müller", country = "Duitsland", language = "en" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("en", (await created.JsonAsync()).GetProperty("language").GetString());

        // Alleen-lezen: geen klant, taal of factuur meer wijzigen.
        await TestApi.ExpireTrialAsync(factory, s.Workspace);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await s.Client.PutAsJsonAsync($"/api/customers/{s.English}", new { name = "Aoife Murphy", language = "nl" })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await PutAsync(s.Client, id, inv => inv["language"] = "nl")).StatusCode);
        Assert.Equal("en", await LanguageAsync(s.Client, id));
        Assert.Equal("en", (await s.Client.GetFromJsonAsync<JsonElement>($"/api/customers/{s.English}")).GetProperty("language").GetString());
    }
}

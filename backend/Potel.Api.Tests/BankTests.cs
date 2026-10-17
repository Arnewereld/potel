using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

static class BankKit
{
    public static string Today(int daysAgo = 0) => DateTime.Today.AddDays(-daysAgo).ToString("yyyy-MM-dd");
    public static string IngDay(int daysAgo = 0) => DateTime.Today.AddDays(-daysAgo).ToString("yyyyMMdd");

    public static Task<HttpResponseMessage> ImportAsync(HttpClient c, string content, string fileName = "afschrift.xml", object? mapping = null) =>
        c.PostAsJsonAsync("/api/bank/import", new { fileName, data = Convert.ToBase64String(StatementKit.Bytes(content)), mapping });

    public static async Task<JsonElement> ImportedAsync(HttpClient c, string content, string fileName = "afschrift.xml", object? mapping = null)
    {
        var res = await ImportAsync(c, content, fileName, mapping);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await res.JsonAsync();
    }

    public static Task<JsonElement> ListAsync(HttpClient c, string filter = "open", string? q = null) =>
        c.GetFromJsonAsync<JsonElement>($"/api/bank?filter={filter}{(q is null ? "" : $"&q={Uri.EscapeDataString(q)}")}");

    public static List<JsonElement> Rows(JsonElement list) => list.GetProperty("transactions").EnumerateArray().ToList();

    public static JsonElement Row(JsonElement list, decimal amount) => Rows(list).Single(t => t.GetProperty("amount").GetDecimal() == amount);

    public static List<JsonElement> Proposals(JsonElement row) => row.GetProperty("proposals").EnumerateArray().ToList();

    public static int Id(JsonElement row) => row.GetProperty("id").GetInt32();

    public static Task<HttpResponseMessage> LinkInvoiceAsync(HttpClient c, int txId, int invoiceId, int? paymentId = null, bool rememberIban = false) =>
        c.PostAsJsonAsync($"/api/bank/transactions/{txId}/invoice", new { invoiceId, paymentId, rememberIban });

    public static async Task<string> NumberAsync(HttpClient c, int invoiceId) => (await GetInvoiceAsync(c, invoiceId)).GetProperty("number").GetString()!;
}

// De pagina Bank: een afschrift inlezen, voorstellen om te koppelen, en de koppelingen zelf. Alleen een beheerder, en
// bedragen komen altijd uit de bank.
public class BankTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    async Task<(HttpClient C, int Customer, int Invoice, string Number)> SetupAsync(string prefix, string? customerIban = null)
    {
        var c = await ReadyAsync(factory, prefix);
        var customer = await CustomerAsync(c, "Klant");
        if (customerIban is not null)
        {
            (await c.PutAsJsonAsync($"/api/customers/{customer}", new
            {
                name = "Klant", company = "Klant B.V.", address = "Dorpsstraat 1", city = "1234 AB Utrecht", country = "Nederland", iban = customerIban,
            })).EnsureSuccessStatusCode();
        }
        var invoice = await InvoiceAsync(c, customer, "verzonden", 1, 100);
        return (c, customer, invoice, await BankKit.NumberAsync(c, invoice));
    }

    [Fact]
    public async Task A_camt_statement_is_imported_once_and_proposes_the_invoice_by_number()
    {
        var (c, _, invoice, number) = await SetupAsync("bank-camt");
        var xml = StatementKit.Camt(
            StatementKit.Entry(121m, "CRDT", BankKit.Today(1), "Iemand", StatementKit.KlantIban, $"Betaling factuur {number}", "REF-A"),
            StatementKit.Entry(49.95m, "DBIT", BankKit.Today(1), "Coolblue", StatementKit.ShopIban, "Bestelling", "REF-B"));
        var first = await BankKit.ImportedAsync(c, xml);
        Assert.Equal("CAMT.053", first.GetProperty("format").GetString());
        Assert.Equal(2, first.GetProperty("added").GetInt32());

        // Nog eens hetzelfde bestand: niets dubbel.
        var again = await BankKit.ImportedAsync(c, xml);
        Assert.Equal(0, again.GetProperty("added").GetInt32());
        Assert.Equal(2, again.GetProperty("skipped").GetInt32());
        Assert.Equal(JsonValueKind.Null, again.GetProperty("importId").ValueKind);

        var list = await BankKit.ListAsync(c);
        Assert.Equal(2, list.GetProperty("counts").GetProperty("open").GetInt32());
        // Een import zonder nieuwe regels komt niet in de lijst.
        Assert.Single(list.GetProperty("imports").EnumerateArray().ToList());
        var incoming = BankKit.Row(list, 121m);
        var proposal = Assert.Single(BankKit.Proposals(incoming));
        Assert.Equal("invoice", proposal.GetProperty("kind").GetString());
        Assert.Equal(invoice, proposal.GetProperty("invoiceId").GetInt32());
        Assert.Equal("factuurnummer", proposal.GetProperty("reason").GetString());
        Assert.False(proposal.GetProperty("partial").GetBoolean());
        // Een afschrijving zonder uitgave: alvast ingevuld om er een uitgave van te maken.
        var outgoing = BankKit.Row(list, -49.95m);
        Assert.Equal(49.95m, outgoing.GetProperty("newExpense").GetProperty("amount").GetDecimal());
        Assert.Equal("Coolblue", outgoing.GetProperty("newExpense").GetProperty("supplier").GetString());

        // Zoeken op naam en op bedrag.
        Assert.Single(BankKit.Rows(await BankKit.ListAsync(c, "alle", "coolblue")));
        Assert.Single(BankKit.Rows(await BankKit.ListAsync(c, "alle", "121,00")));
    }

    [Fact]
    public async Task Linking_records_a_bank_payment_with_the_amount_from_the_bank_and_unlinking_takes_it_back()
    {
        var (c, _, invoice, number) = await SetupAsync("bank-koppel");
        var ws = await TestApi.WorkspaceIdAsync(c);
        await WorkflowKit.CreateWorkflowAsync(c, "Bij betaling", WorkflowKit.GraphJson([WorkflowKit.Node("s", "trigger.paid")], []), active: true);
        await BankKit.ImportedAsync(c, StatementKit.Ing(
            StatementKit.IngRow(BankKit.IngDay(2), "Klant B.V.", StatementKit.KlantIban, "Bij", "121,00", $"Factuur {number}")), "ing.csv");
        var tx = BankKit.Id(BankKit.Rows(await BankKit.ListAsync(c)).Single());

        // Een bedrag van de browser telt niet: de bank zegt 121.
        var res = await c.PostAsJsonAsync($"/api/bank/transactions/{tx}/invoice", new { invoiceId = invoice, amount = 1, rememberIban = false });
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Assert.True((await res.JsonAsync()).GetProperty("becamePaid").GetBoolean());
        var inv = await GetInvoiceAsync(c, invoice);
        Assert.Equal("betaald", inv.GetProperty("status").GetString());
        var payment = Assert.Single(PaymentKit.Payments(inv));
        Assert.Equal(121m, payment.GetProperty("amount").GetDecimal());
        Assert.Equal("bank", payment.GetProperty("method").GetString());
        Assert.Equal(tx, payment.GetProperty("bankTransactionId").GetInt32());
        Assert.StartsWith(BankKit.Today(2), payment.GetProperty("date").GetString());
        Assert.Equal(1, WorkflowKit.InWorkspace(factory, ws, db => db.WorkflowRuns.Count()));

        var linked = await BankKit.ListAsync(c, "gekoppeld");
        var row = BankKit.Rows(linked).Single();
        Assert.Equal("factuur", row.GetProperty("state").GetString());
        Assert.Equal(number, row.GetProperty("link").GetProperty("number").GetString());
        Assert.Equal(0, linked.GetProperty("counts").GetProperty("open").GetInt32());

        // Twee keer koppelen kan niet.
        Assert.Equal(HttpStatusCode.Conflict, (await BankKit.LinkInvoiceAsync(c, tx, invoice)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/api/bank/transactions/{tx}/mark", new { mark = "prive" })).StatusCode);

        // Ongedaan maken haalt de betaling weg en de factuur staat weer open.
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/bank/transactions/{tx}/unlink", null)).StatusCode);
        inv = await GetInvoiceAsync(c, invoice);
        Assert.Equal("verzonden", inv.GetProperty("status").GetString());
        Assert.Empty(PaymentKit.Payments(inv));
        Assert.Equal(1, (await BankKit.ListAsync(c)).GetProperty("counts").GetProperty("open").GetInt32());
    }

    [Fact]
    public async Task A_smaller_amount_is_a_partial_payment_and_a_larger_one_is_refused()
    {
        var (c, _, invoice, _) = await SetupAsync("bank-deel", StatementKit.KlantIban);
        await BankKit.ImportedAsync(c, StatementKit.Camt(
            StatementKit.Entry(50m, "CRDT", BankKit.Today(3), "Klant", StatementKit.KlantIban, "Eerste deel", "D1"),
            StatementKit.Entry(500m, "CRDT", BankKit.Today(2), "Klant", StatementKit.KlantIban, "Veel te veel", "D2")));
        var list = await BankKit.ListAsync(c);
        var part = BankKit.Row(list, 50m);
        var proposal = Assert.Single(BankKit.Proposals(part));
        Assert.True(proposal.GetProperty("partial").GetBoolean());
        Assert.Equal(invoice, proposal.GetProperty("invoiceId").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await BankKit.LinkInvoiceAsync(c, BankKit.Id(part), invoice)).StatusCode);
        var inv = await GetInvoiceAsync(c, invoice);
        Assert.Equal("verzonden", inv.GetProperty("status").GetString());
        Assert.Equal(71m, inv.GetProperty("openAmount").GetDecimal());

        var tooMuch = await BankKit.LinkInvoiceAsync(c, BankKit.Id(BankKit.Row(list, 500m)), invoice);
        Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
        Assert.StartsWith(Invoicing.InvoicePayments.OverpaidPrefix, await tooMuch.ErrorAsync());
        Assert.Single(PaymentKit.Payments(await GetInvoiceAsync(c, invoice)));
    }

    [Fact]
    public async Task A_payment_you_entered_yourself_is_linked_instead_of_doubled_and_stays_on_unlink()
    {
        var (c, customer, invoice, _) = await SetupAsync("bank-zelf");
        var paid = await PaymentKit.PaidAsync(c, invoice, 121, BankKit.Today(4), "bank");
        var paymentId = PaymentKit.Payments(paid).Single().GetProperty("id").GetInt32();
        await BankKit.ImportedAsync(c, StatementKit.Camt(
            StatementKit.Entry(121m, "CRDT", BankKit.Today(1), "KLANT BV", StatementKit.KlantIban, "Bedankt", "Z1")));
        var row = BankKit.Rows(await BankKit.ListAsync(c)).Single();
        var proposal = Assert.Single(BankKit.Proposals(row));
        Assert.Equal("payment", proposal.GetProperty("kind").GetString());
        Assert.Equal(paymentId, proposal.GetProperty("paymentId").GetInt32());

        // Zonder betaling koppelen kan niet meer: de factuur is al betaald.
        Assert.Equal(HttpStatusCode.Conflict, (await BankKit.LinkInvoiceAsync(c, BankKit.Id(row), invoice)).StatusCode);
        var res = await BankKit.LinkInvoiceAsync(c, BankKit.Id(row), invoice, paymentId, rememberIban: true);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var payments = PaymentKit.Payments(await GetInvoiceAsync(c, invoice));
        Assert.Equal(BankKit.Id(row), Assert.Single(payments).GetProperty("bankTransactionId").GetInt32());
        // Het rekeningnummer staat nu bij de klant.
        Assert.Equal(StatementKit.KlantIban, (await c.GetFromJsonAsync<JsonElement>($"/api/customers/{customer}")).GetProperty("iban").GetString());

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/bank/transactions/{BankKit.Id(row)}/unlink", null)).StatusCode);
        var after = await GetInvoiceAsync(c, invoice);
        Assert.Equal("betaald", after.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, Assert.Single(PaymentKit.Payments(after)).GetProperty("bankTransactionId").ValueKind);
    }

    [Fact]
    public async Task Two_links_at_the_same_time_record_one_payment()
    {
        var (c, _, invoice, _) = await SetupAsync("bank-race");
        await BankKit.ImportedAsync(c, StatementKit.Camt(StatementKit.Entry(30m, "CRDT", BankKit.Today(1), "Klant", null, "Deel", "R1")));
        var tx = BankKit.Id(BankKit.Rows(await BankKit.ListAsync(c)).Single());
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => SafeSend(() => BankKit.LinkInvoiceAsync(c, tx, invoice))));
        Assert.Equal(1, results.Count(r => r?.StatusCode == HttpStatusCode.OK));
        Assert.Single(PaymentKit.Payments(await GetInvoiceAsync(c, invoice)));
    }

    [Fact]
    public async Task An_unknown_csv_asks_for_columns_and_remembers_them()
    {
        var c = await ReadyAsync(factory, "bank-kolommen");
        var csv = $"Boekdatum;Wie;Bedrag;Omschrijving\n{DateTime.Today.AddDays(-1):dd-MM-yyyy};Shop;-25,10;Inkt\n";
        var ask = await BankKit.ImportedAsync(c, csv, "eigen.csv");
        Assert.True(ask.GetProperty("needsMapping").GetBoolean());
        Assert.Equal(["Boekdatum", "Wie", "Bedrag", "Omschrijving"], ask.GetProperty("headers").EnumerateArray().Select(h => h.GetString()));
        Assert.Equal(JsonValueKind.Null, ask.GetProperty("mapping").ValueKind);

        var missing = await BankKit.ImportAsync(c, csv, "eigen.csv", new { date = "", amount = "Bedrag" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        var done = await BankKit.ImportedAsync(c, csv, "eigen.csv", new { date = "Boekdatum", amount = "Bedrag", description = "Omschrijving", name = "Wie" });
        Assert.Equal(1, done.GetProperty("added").GetInt32());
        Assert.Equal(-25.10m, BankKit.Rows(await BankKit.ListAsync(c)).Single().GetProperty("amount").GetDecimal());

        // De volgende keer gaat het vanzelf met dezelfde kolommen.
        var next = $"Boekdatum;Wie;Bedrag;Omschrijving\n{DateTime.Today:dd-MM-yyyy};Bakker;-3,00;Brood\n";
        var auto = await BankKit.ImportedAsync(c, next, "eigen2.csv");
        Assert.False(auto.GetProperty("needsMapping").GetBoolean());
        Assert.Equal(1, auto.GetProperty("added").GetInt32());
        Assert.Equal("Boekdatum", (await BankKit.ListAsync(c)).GetProperty("mapping").GetProperty("date").GetString());

        // Een import ongedaan maken haalt zijn regels weg.
        var importId = auto.GetProperty("importId").GetInt32();
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/bank/imports/{importId}")).StatusCode);
        Assert.Single(BankKit.Rows(await BankKit.ListAsync(c)));
    }

    [Fact]
    public async Task Bad_files_get_a_friendly_error()
    {
        var c = await ReadyAsync(factory, "bank-fout");
        var xxe = "<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><Document><BkToCstmrStmt><Stmt>&x;</Stmt></BkToCstmrStmt></Document>";
        var res = await BankKit.ImportAsync(c, xxe);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(Banking.StatementParser.NotAStatement, await res.ErrorAsync());
        var notBase64 = await c.PostAsJsonAsync("/api/bank/import", new { fileName = "x", data = "!!geen base64!!" });
        Assert.Equal(HttpStatusCode.BadRequest, notBase64.StatusCode);
        var broken = await BankKit.ImportAsync(c, StatementKit.Ing(StatementKit.IngRow("gisteren", "X", "", "Bij", "1,00", "")), "ing.csv");
        Assert.Contains("Regel 2", await broken.ErrorAsync());
        var empty = await BankKit.ImportAsync(c, StatementKit.Camt());
        Assert.Equal("In dit afschrift staan geen transacties.", await empty.ErrorAsync());
        Assert.Equal(0, (await BankKit.ListAsync(c, "alle")).GetProperty("counts").GetProperty("all").GetInt32());
    }

    [Fact]
    public async Task Outgoing_money_links_to_an_expense_or_becomes_one_with_the_amount_from_the_bank()
    {
        var c = await ReadyAsync(factory, "bank-kosten");
        var expense = await ExpenseKit.CreateAsync(c, ExpenseKit.Body(amount: 49.95m, supplier: "Coolblue", date: BankKit.Today(10)));
        await BankKit.ImportedAsync(c, StatementKit.Camt(
            StatementKit.Entry(49.95m, "DBIT", BankKit.Today(2), "COOLBLUE B.V.", StatementKit.ShopIban, "Bestelling", "K1"),
            StatementKit.Entry(12.10m, "DBIT", BankKit.Today(1), "COOLBLUE B.V.", StatementKit.ShopIban, "Kabel", "K2"),
            StatementKit.Entry(80m, "DBIT", BankKit.Today(1), "Supermarkt", null, "Boodschappen", "K3")));
        var list = await BankKit.ListAsync(c);
        var first = BankKit.Row(list, -49.95m);
        var proposal = Assert.Single(BankKit.Proposals(first));
        Assert.Equal(expense.GetProperty("id").GetInt32(), proposal.GetProperty("expenseId").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/bank/transactions/{BankKit.Id(first)}/expense", new { expenseId = proposal.GetProperty("expenseId").GetInt32() })).StatusCode);

        // Maak kost: categorie en btw van de vorige uitgave bij Coolblue; het bedrag komt uit de bank, niet uit de browser.
        var second = BankKit.Row(list, -12.10m);
        Assert.Equal("hardware", second.GetProperty("newExpense").GetProperty("category").GetString());
        var made = await c.PostAsJsonAsync($"/api/bank/transactions/{BankKit.Id(second)}/create-expense",
            new { supplier = "Coolblue", description = "Kabel", category = "hardware", vatRate = 21, amount = 9999 });
        Assert.True(made.StatusCode == HttpStatusCode.OK, await made.Content.ReadAsStringAsync());
        var newId = (await made.JsonAsync()).GetProperty("expenseId").GetInt32();
        var created = await c.GetFromJsonAsync<JsonElement>($"/api/expenses/{newId}");
        Assert.Equal(12.10m, created.GetProperty("amount").GetDecimal());
        Assert.Equal(2.10m, created.GetProperty("vatAmount").GetDecimal());
        Assert.StartsWith(BankKit.Today(1), created.GetProperty("date").GetString());

        var noCategory = await c.PostAsJsonAsync($"/api/bank/transactions/{BankKit.Id(BankKit.Row(list, -80m))}/create-expense", new { supplier = "Supermarkt", vatRate = 9 });
        Assert.Equal("Kies een categorie.", await noCategory.ErrorAsync());
        // Privé: geen uitgave, wel weg uit de lijst om te koppelen.
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/bank/transactions/{BankKit.Id(BankKit.Row(list, -80m))}/mark", new { mark = "prive" })).StatusCode);
        var counts = (await BankKit.ListAsync(c)).GetProperty("counts");
        Assert.Equal(0, counts.GetProperty("open").GetInt32());
        Assert.Equal(2, counts.GetProperty("linked").GetInt32());
        Assert.Equal(1, counts.GetProperty("prive").GetInt32());

        // Een import waaruit al gekoppeld is, kun je niet ongedaan maken.
        var importId = (await BankKit.ListAsync(c, "alle")).GetProperty("imports")[0].GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync($"/api/bank/imports/{importId}")).StatusCode);

        // De uitgave weghalen zet de regel weer open; ongedaan maken laat de gemaakte uitgave staan.
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/api/expenses/{expense.GetProperty("id").GetInt32()}")).StatusCode);
        Assert.Equal(1, (await BankKit.ListAsync(c)).GetProperty("counts").GetProperty("open").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/api/bank/transactions/{BankKit.Id(second)}/unlink", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync($"/api/expenses/{newId}")).StatusCode);
        Assert.Equal(2, (await BankKit.ListAsync(c)).GetProperty("counts").GetProperty("open").GetInt32());
    }

    [Fact]
    public async Task Another_workspace_cannot_see_or_use_your_bank_lines_invoices_or_expenses()
    {
        var (a, _, invoiceA, _) = await SetupAsync("bank-a");
        var expenseA = (await ExpenseKit.CreateAsync(a, ExpenseKit.Body(amount: 10m, date: BankKit.Today(3)))).GetProperty("id").GetInt32();
        await BankKit.ImportedAsync(a, StatementKit.Camt(StatementKit.Entry(121m, "CRDT", BankKit.Today(1), "Klant", null, "x", "A1")));
        var txA = BankKit.Id(BankKit.Rows(await BankKit.ListAsync(a)).Single());

        var (b, _, invoiceB, _) = await SetupAsync("bank-b");
        await BankKit.ImportedAsync(b, StatementKit.Camt(
            StatementKit.Entry(121m, "CRDT", BankKit.Today(1), "Klant", null, "x", "A1"),
            StatementKit.Entry(10m, "DBIT", BankKit.Today(1), "Shop", null, "y", "A2")));
        var listB = await BankKit.ListAsync(b);
        // Hetzelfde kenmerk in een andere werkruimte is gewoon een nieuwe regel.
        Assert.Equal(2, BankKit.Rows(listB).Count);
        var inB = BankKit.Id(BankKit.Row(listB, 121m));
        var outB = BankKit.Id(BankKit.Row(listB, -10m));

        Assert.Equal(HttpStatusCode.NotFound, (await BankKit.LinkInvoiceAsync(b, inB, invoiceA)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync($"/api/bank/transactions/{outB}/expense", new { expenseId = expenseA })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await BankKit.LinkInvoiceAsync(b, txA, invoiceB)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/api/bank/transactions/{txA}/unlink", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync($"/api/bank/transactions/{txA}/mark", new { mark = "negeren" })).StatusCode);
        Assert.DoesNotContain(BankKit.Rows(await BankKit.ListAsync(b, "alle")), r => BankKit.Id(r) == txA);
        Assert.Single(BankKit.Rows(await BankKit.ListAsync(a, "alle")));
        Assert.Empty(PaymentKit.Payments(await GetInvoiceAsync(a, invoiceA)));
    }

    [Fact]
    public async Task Only_an_admin_can_use_the_bank_page_and_a_read_only_workspace_cannot_import()
    {
        var c = await ReadyAsync(factory, "bank-rol");
        var email = $"mw-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(c, email);
        var member = await TestApi.LoginAsync(factory, email);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/bank")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await BankKit.ImportAsync(member, StatementKit.Camt(StatementKit.Entry(1m, "CRDT", BankKit.Today(), "X", null, "x", "M1")))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsync("/api/bank/transactions/1/unlink", null)).StatusCode);

        await TestApi.ExpireTrialAsync(factory, await TestApi.WorkspaceIdAsync(c));
        Assert.Equal(HttpStatusCode.PaymentRequired, (await BankKit.ImportAsync(c, StatementKit.Camt(StatementKit.Entry(1m, "CRDT", BankKit.Today(), "X", null, "x", "M2")))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/bank")).StatusCode);
    }

    [Fact]
    public async Task Demo_invoices_cannot_be_linked_and_customer_ibans_are_checked()
    {
        var c = await ReadyAsync(factory, "bank-demo");
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/workspace/demo-data", null)).StatusCode);
        var demo = (await c.GetFromJsonAsync<List<JsonElement>>("/api/invoices"))!.First(i => i.GetProperty("status").GetString() is "verzonden" or "verlopen");
        await BankKit.ImportedAsync(c, StatementKit.Camt(StatementKit.Entry(1m, "CRDT", BankKit.Today(1), "X", null, "x", "DM1")));
        var tx = BankKit.Id(BankKit.Rows(await BankKit.ListAsync(c)).Single());
        var res = await BankKit.LinkInvoiceAsync(c, tx, demo.GetProperty("id").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("voorbeeldfactuur", await res.ErrorAsync());

        var bad = await c.PostAsJsonAsync("/api/customers", new { name = "Fout", iban = "NL03ABNA0123456789" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var good = await c.PostAsJsonAsync("/api/customers", new { name = "Goed", iban = "nl02 abna 0123 4567 89" });
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        Assert.Equal(StatementKit.KlantIban, (await good.JsonAsync()).GetProperty("iban").GetString());
    }

    [Fact]
    public async Task The_export_contains_the_bank_lines_and_deleting_the_workspace_removes_them()
    {
        var c = await ReadyAsync(factory, "bank-export");
        await BankKit.ImportedAsync(c, StatementKit.Camt(StatementKit.Entry(5m, "CRDT", BankKit.Today(1), "X", null, "x", "E1")));
        var export = await c.GetFromJsonAsync<JsonElement>("/api/workspace/export");
        var line = Assert.Single(export.GetProperty("bankTransactions").EnumerateArray().ToList());
        Assert.Equal(5m, line.GetProperty("amount").GetDecimal());
        Assert.False(line.TryGetProperty("dedupeKey", out _));
        Assert.Single(export.GetProperty("bankImports").EnumerateArray().ToList());

        var ws = await TestApi.WorkspaceIdAsync(c);
        var del = new HttpRequestMessage(HttpMethod.Delete, "/api/workspace") { Content = JsonContent.Create(new { password = "geheim123" }) };
        Assert.True((await c.SendAsync(del)).IsSuccessStatusCode);
        Assert.Equal(0, WorkflowKit.InWorkspace(factory, ws, db => db.BankTransactions.Count() + db.BankImports.Count()));
    }
}

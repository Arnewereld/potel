using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Invoicing;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

static class PaymentKit
{
    public static Task<HttpResponseMessage> PayAsync(HttpClient c, int invoiceId, decimal amount, string? date = null, string? method = null, string? note = null) =>
        c.PostAsJsonAsync($"/api/invoices/{invoiceId}/payments", new { amount, date = date ?? Day(DateTime.Today), method, note });

    public static async Task<JsonElement> PaidAsync(HttpClient c, int invoiceId, decimal amount, string? date = null, string? method = null)
    {
        var res = await PayAsync(c, invoiceId, amount, date, method);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await res.JsonAsync();
    }

    public static List<JsonElement> Payments(JsonElement invoice) => invoice.GetProperty("payments").EnumerateArray().ToList();

    // Zet de vervaldatum in het verleden, zoals bij een factuur van een maand geleden.
    public static void Backdate(PortalFactory f, int workspaceId, int invoiceId)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        db.Tenant.WorkspaceId = workspaceId;
        var inv = db.Invoices.Single(i => i.Id == invoiceId);
        inv.IssueDate = DateTime.UtcNow.Date.AddDays(-30);
        inv.DueDate = DateTime.UtcNow.Date.AddDays(-16);
        db.SaveChanges();
    }
}

// Deelbetalingen: elke betaling haalt iets van het openstaande bedrag af, de laatste maakt de factuur betaald, en wie een
// betaling weghaalt zet hem terug. Meer betalen dan er openstaat kan niet.
public class PaymentTests(PortalFactory factory) : IClassFixture<PortalFactory>
{
    int Runs(int workspaceId) => WorkflowKit.InWorkspace(factory, workspaceId, db => db.WorkflowRuns.Count());

    [Fact]
    public async Task Partial_payments_lower_the_open_amount_and_the_last_one_makes_the_invoice_paid()
    {
        var c = await ReadyAsync(factory, "deelbetaling");
        var ws = await TestApi.WorkspaceIdAsync(c);
        await WorkflowKit.CreateWorkflowAsync(c, "Bij betaling", WorkflowKit.GraphJson([WorkflowKit.Node("s", "trigger.paid")], []), active: true);
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 100);

        var first = await PaymentKit.PaidAsync(c, id, 50, "2026-10-01", "bank");
        Assert.Equal("verzonden", first.GetProperty("status").GetString());
        Assert.Equal(71m, first.GetProperty("openAmount").GetDecimal());
        Assert.Equal(50m, first.GetProperty("paidTotal").GetDecimal());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("paidAt").ValueKind);
        var p = Assert.Single(PaymentKit.Payments(first));
        Assert.Equal("bank", p.GetProperty("method").GetString());
        Assert.StartsWith("2026-10-01", p.GetProperty("date").GetString());
        Assert.Equal(0, Runs(ws));
        // In de lijst en op het dashboard telt alleen wat nog openstaat.
        var listed = (await c.GetFromJsonAsync<List<JsonElement>>("/api/invoices"))!.Single(i => i.GetProperty("id").GetInt32() == id);
        Assert.Equal(71m, listed.GetProperty("openAmount").GetDecimal());
        Assert.Equal(71m, (await c.GetFromJsonAsync<JsonElement>("/api/dashboard")).GetProperty("outstanding").GetDecimal());

        var paid = await PaymentKit.PaidAsync(c, id, 71, "2026-10-05");
        Assert.Equal("betaald", paid.GetProperty("status").GetString());
        Assert.StartsWith("2026-10-05", paid.GetProperty("paidAt").GetString());
        Assert.Equal(0m, paid.GetProperty("openAmount").GetDecimal());
        Assert.Equal(2, PaymentKit.Payments(paid).Count);
        Assert.Equal("handmatig", PaymentKit.Payments(paid)[1].GetProperty("method").GetString());
        // De werkstroom "Factuur betaald" start één keer, bij de laatste betaling.
        Assert.Equal(1, Runs(ws));

        // Op een betaalde factuur valt niets meer te betalen.
        var again = await PaymentKit.PayAsync(c, id, 1);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(InvoicePayments.AlreadyPaidError, await again.ErrorAsync());
    }

    [Fact]
    public async Task Paying_more_than_is_open_is_refused_with_a_clear_message()
    {
        var c = await ReadyAsync(factory, "te-veel");
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 100);
        await PaymentKit.PaidAsync(c, id, 21);
        var res = await PaymentKit.PayAsync(c, id, 100.01m);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var error = await res.ErrorAsync();
        Assert.StartsWith(InvoicePayments.OverpaidPrefix, error);
        Assert.Contains("€ 100,00", error);
        Assert.Single(PaymentKit.Payments(await GetInvoiceAsync(c, id)));
        // Precies het restant mag wel; bedragen gaan op centen.
        Assert.Equal("betaald", (await PaymentKit.PaidAsync(c, id, 99.999m)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Removing_a_payment_reopens_the_invoice_as_sent_or_overdue()
    {
        var c = await ReadyAsync(factory, "weghalen");
        var ws = await TestApi.WorkspaceIdAsync(c);
        var customer = await CustomerAsync(c);
        var fresh = await InvoiceAsync(c, customer, "verzonden", 1, 100);
        var late = await InvoiceAsync(c, customer, "verzonden", 1, 100);
        PaymentKit.Backdate(factory, ws, late);

        foreach (var (id, expected) in new[] { (fresh, "verzonden"), (late, "verlopen") })
        {
            await PaymentKit.PaidAsync(c, id, 21, "2026-10-01");
            var paid = await PaymentKit.PaidAsync(c, id, 100, "2026-10-03");
            Assert.Equal("betaald", paid.GetProperty("status").GetString());
            var last = PaymentKit.Payments(paid)[1].GetProperty("id").GetInt32();

            var res = await c.DeleteAsync($"/api/invoices/{id}/payments/{last}");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var reopened = await res.JsonAsync();
            Assert.Equal(expected, reopened.GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, reopened.GetProperty("paidAt").ValueKind);
            Assert.Equal(100m, reopened.GetProperty("openAmount").GetDecimal());
            Assert.Single(PaymentKit.Payments(reopened));
        }
        // Een betaling van een andere factuur haal je niet via deze factuur weg.
        var other = PaymentKit.Payments(await GetInvoiceAsync(c, late))[0].GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/api/invoices/{fresh}/payments/{other}")).StatusCode);
    }

    [Fact]
    public async Task Marking_paid_records_one_payment_for_what_is_still_open()
    {
        var c = await ReadyAsync(factory, "markeer-betaald");
        var customer = await CustomerAsync(c);
        var whole = await InvoiceAsync(c, customer, "verzonden", 1, 100);
        var res = await c.PostAsJsonAsync($"/api/invoices/{whole}/status", new { status = "betaald", paidAt = "2026-10-10" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var p = Assert.Single(PaymentKit.Payments(await res.JsonAsync()));
        Assert.Equal(121m, p.GetProperty("amount").GetDecimal());
        Assert.Equal("handmatig", p.GetProperty("method").GetString());
        Assert.StartsWith("2026-10-10", p.GetProperty("date").GetString());

        var rest = await InvoiceAsync(c, customer, "verzonden", 1, 100);
        await PaymentKit.PaidAsync(c, rest, 40, "2026-10-02");
        var marked = await (await c.PostAsJsonAsync($"/api/invoices/{rest}/status", new { status = "betaald", paidAt = "2026-10-11" })).JsonAsync();
        Assert.Equal("betaald", marked.GetProperty("status").GetString());
        Assert.StartsWith("2026-10-11", marked.GetProperty("paidAt").GetString());
        Assert.Equal([40m, 81m], PaymentKit.Payments(marked).Select(x => x.GetProperty("amount").GetDecimal()));

        // Een nieuwe factuur die meteen als betaald wordt vastgelegd, krijgt ook zijn betaling.
        var direct = await GetInvoiceAsync(c, await InvoiceAsync(c, customer, "betaald", 2, 50));
        Assert.Equal(121m, Assert.Single(PaymentKit.Payments(direct)).GetProperty("amount").GetDecimal());

        // Een creditnota verrekenen is geen betaling.
        var credit = (await (await c.PostAsync($"/api/invoices/{rest}/credit", null)).JsonAsync()).GetProperty("id").GetInt32();
        (await SetStatusAsync(c, credit, "verzonden")).EnsureSuccessStatusCode();
        var settled = await (await SetStatusAsync(c, credit, "betaald")).JsonAsync();
        Assert.Empty(PaymentKit.Payments(settled));
    }

    [Fact]
    public async Task Drafts_credit_notes_and_bad_amounts_or_dates_cannot_be_paid()
    {
        var c = await ReadyAsync(factory, "niet-betalen");
        var customer = await CustomerAsync(c);
        var draft = await InvoiceAsync(c, customer, "concept", 1, 100);
        Assert.Contains("Verstuur de factuur eerst", await (await PaymentKit.PayAsync(c, draft, 10)).ErrorAsync());

        var sent = await InvoiceAsync(c, customer, "verzonden", 1, 100);
        var credit = (await (await c.PostAsync($"/api/invoices/{sent}/credit", null)).JsonAsync()).GetProperty("id").GetInt32();
        var draftCredit = await GetInvoiceAsync(c, credit);
        (await c.PutAsJsonAsync($"/api/invoices/{credit}", WithLines(draftCredit, new[] { new { description = "Deels", quantity = -1, unit = "stuk", unitPrice = 20, vatRate = 21 } })))
            .EnsureSuccessStatusCode();
        (await SetStatusAsync(c, credit, "verzonden")).EnsureSuccessStatusCode();
        Assert.Contains("creditnota", await (await PaymentKit.PayAsync(c, credit, 10)).ErrorAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await PaymentKit.PayAsync(c, sent, 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PaymentKit.PayAsync(c, sent, -5)).StatusCode);
        Assert.Contains("toekomst", await (await PaymentKit.PayAsync(c, sent, 5, Day(DateTime.Today.AddDays(5)))).ErrorAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await PaymentKit.PayAsync(c, sent, 5, method: "contant")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PaymentKit.PayAsync(c, sent, 5, note: new string('x', 501))).StatusCode);
        // Na de creditnota van € 24,20 staat er nog € 96,80 open.
        Assert.StartsWith(InvoicePayments.OverpaidPrefix, await (await PaymentKit.PayAsync(c, sent, 100)).ErrorAsync());
        Assert.Empty(PaymentKit.Payments(await GetInvoiceAsync(c, sent)));
        Assert.Equal(HttpStatusCode.NotFound, (await PaymentKit.PayAsync(c, 999_999, 5)).StatusCode);
    }

    [Fact]
    public async Task A_credit_note_that_covers_the_rest_of_a_partly_paid_invoice_settles_it()
    {
        var c = await ReadyAsync(factory, "credit-restant");
        var ws = await TestApi.WorkspaceIdAsync(c);
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 100);
        await PaymentKit.PaidAsync(c, id, 96.8m, "2026-10-04");
        PaymentKit.Backdate(factory, ws, id);
        var credit = (await (await c.PostAsync($"/api/invoices/{id}/credit", null)).JsonAsync()).GetProperty("id").GetInt32();
        var draft = await GetInvoiceAsync(c, credit);
        (await c.PutAsJsonAsync($"/api/invoices/{credit}", WithLines(draft, new[] { new { description = "Korting", quantity = -1, unit = "stuk", unitPrice = 20, vatRate = 21 } })))
            .EnsureSuccessStatusCode();
        (await SetStatusAsync(c, credit, "verzonden")).EnsureSuccessStatusCode();

        var inv = await GetInvoiceAsync(c, id);
        Assert.Equal("betaald", inv.GetProperty("status").GetString());
        Assert.StartsWith("2026-10-04", inv.GetProperty("paidAt").GetString());
        // Niet meer te laat, dus ook geen werkstroom "Factuur te laat".
        await c.GetAsync("/api/invoices");
        Assert.Equal("betaald", (await GetInvoiceAsync(c, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_vat_return_follows_the_invoice_not_the_payments()
    {
        var c = await ReadyAsync(factory, "btw-betaling");
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 1000);
        var quarter = (DateTime.Today.Month - 1) / 3 + 1;
        var url = $"/api/tax/vat-return?year={DateTime.Today.Year}&quarter={quarter}";
        var before = (await c.GetStringAsync(url));
        await PaymentKit.PaidAsync(c, id, 100);
        Assert.Equal(before, await c.GetStringAsync(url));
        await PaymentKit.PaidAsync(c, id, 1110);
        Assert.Equal(before, await c.GetStringAsync(url));
    }

    [Fact]
    public async Task Another_workspace_cannot_see_add_or_remove_payments()
    {
        var a = await ReadyAsync(factory, "betaling-a");
        var b = await ReadyAsync(factory, "betaling-b");
        var id = await InvoiceAsync(a, await CustomerAsync(a), "verzonden", 1, 100);
        var paid = await PaymentKit.PaidAsync(a, id, 10);
        var paymentId = PaymentKit.Payments(paid)[0].GetProperty("id").GetInt32();

        Assert.Equal(HttpStatusCode.NotFound, (await PaymentKit.PayAsync(b, id, 10)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/invoices/{id}/payments/{paymentId}")).StatusCode);
        // Ook niet via een eigen factuur met het id van de betaling van een ander.
        var own = await InvoiceAsync(b, await CustomerAsync(b), "verzonden", 1, 100);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/invoices/{own}/payments/{paymentId}")).StatusCode);
        Assert.Single(PaymentKit.Payments(await GetInvoiceAsync(a, id)));
        Assert.Empty(PaymentKit.Payments(await GetInvoiceAsync(b, own)));
    }

    [Fact]
    public async Task An_employee_records_payments_but_only_an_admin_removes_them_and_read_only_blocks_both()
    {
        var admin = await ReadyAsync(factory, "betaling-rol");
        var ws = await TestApi.WorkspaceIdAsync(admin);
        var email = $"mw-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(admin, email);
        var employee = await TestApi.LoginAsync(factory, email);
        var id = await InvoiceAsync(admin, await CustomerAsync(admin), "verzonden", 1, 100);

        var paid = await PaymentKit.PaidAsync(employee, id, 10);
        var paymentId = PaymentKit.Payments(paid)[0].GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.DeleteAsync($"/api/invoices/{id}/payments/{paymentId}")).StatusCode);

        await TestApi.ExpireTrialAsync(factory, ws);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await PaymentKit.PayAsync(admin, id, 10)).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.DeleteAsync($"/api/invoices/{id}/payments/{paymentId}")).StatusCode);
        Assert.Single(PaymentKit.Payments(await GetInvoiceAsync(admin, id)));
    }

    [Fact]
    public async Task The_public_invoice_page_shows_what_is_paid_and_what_is_still_open()
    {
        var f = factory;
        var c = await ReadyAsync(f, "openbaar-deel");
        var id = await InvoiceAsync(c, await CustomerAsync(c), "verzonden", 1, 100);
        await PaymentKit.PaidAsync(c, id, 21);
        var token = InvoiceMailer.NewToken();
        var ws = await TestApi.WorkspaceIdAsync(c);
        WorkflowKit.InWorkspace(f, ws, db => { db.InvoiceLinks.Add(new InvoiceLink { InvoiceId = id, TokenHash = InvoiceMailer.Hash(token) }); return db.SaveChanges(); });
        var page = await (await f.CreateClient().PostAsJsonAsync("/api/public/invoice", new { token })).JsonAsync();
        Assert.Equal(21m, page.GetProperty("invoice").GetProperty("paidTotal").GetDecimal());
        Assert.Equal(100m, page.GetProperty("invoice").GetProperty("openAmount").GetDecimal());
        Assert.False(page.GetProperty("invoice").TryGetProperty("payments", out _));
    }
}

// Herinneringen gaan over wat er na deelbetalingen nog openstaat, en stoppen zodra alles betaald is.
public class PaymentReminderTests(MailFactory factory) : IClassFixture<MailFactory>
{
    async Task<int> RunRemindersAsync(int workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = workspaceId;
        return await scope.ServiceProvider.GetRequiredService<ReminderService>().RunAsync();
    }

    [Fact]
    public async Task Reminders_mention_the_open_amount_and_skip_fully_paid_invoices()
    {
        factory.Clock.SetDay(2026, 10, 12);
        var c = await ReadyAsync(factory, "herinnering-deel");
        var ws = await TestApi.WorkspaceIdAsync(c);
        var email = $"klant-{Guid.NewGuid():N}@example.com";
        var customer = (await (await c.PostAsJsonAsync("/api/customers", new { name = "Sanne", company = "Bakkerij", email, address = "Dorpsstraat 1", city = "1234 AB Utrecht" })).JsonAsync())
            .GetProperty("id").GetInt32();
        (await c.PutAsJsonAsync("/api/reminders", new { enabled = true, firstDays = 7, secondDays = 14, finalDays = 28 })).EnsureSuccessStatusCode();
        var partly = await InvoiceAsync(c, customer, "verzonden", 1, 1000);
        var paidInParts = await InvoiceAsync(c, customer, "verzonden", 1, 200);
        await PaymentKit.PaidAsync(c, partly, 210, "2026-10-12");
        await PaymentKit.PaidAsync(c, paidInParts, 42, "2026-10-12");
        await PaymentKit.PaidAsync(c, paidInParts, 200, "2026-10-12");

        factory.Clock.SetDay(2026, 11, 2);
        Assert.Equal(1, await RunRemindersAsync(ws));
        var mail = Assert.Single(factory.Mail.To(email));
        Assert.Contains("Er staat nog € 1.000,00 open", mail.Body);
        Assert.Empty(WorkflowKit.InWorkspace(factory, ws, db => db.InvoiceMails.Where(m => m.InvoiceId == paidInParts).ToList()));

        // Ook handmatig: een herinnering voor een betaalde factuur kan niet.
        var manual = await c.PostAsJsonAsync($"/api/invoices/{paidInParts}/mail", new { step = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, manual.StatusCode);

        // De rest komt binnen: geen herinneringen meer.
        await PaymentKit.PaidAsync(c, partly, 1000, "2026-11-03");
        factory.Clock.SetDay(2026, 11, 20);
        Assert.Equal(0, await RunRemindersAsync(ws));
    }
}

// De migratie naar deelbetalingen: een betaalde factuur van daarvoor blijft betaald, met één betaling voor het hele bedrag.
public class PaymentMigrationTests : IDisposable
{
    const string Before = "20261013120243_Offertes";
    readonly string path = Path.Combine(Path.GetTempPath(), $"potel-betalingen-{Guid.NewGuid():N}.db");

    AppDb Db(int workspaceId = 0) =>
        new(new DbContextOptionsBuilder<AppDb>().UseSqlite($"Data Source={path}").Options, new Tenant { WorkspaceId = workspaceId });

    [Fact]
    public void An_existing_paid_invoice_still_reads_as_paid_with_the_right_open_amount()
    {
        using (var db = Db()) db.GetService<IMigrator>().Migrate(Before);
        using (var con = new SqliteConnection($"Data Source={path}"))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            // Regels die op een halve cent afronden (3 × 33,335 = 100,005 en 0,5 × 21% = 0,105), twee btw-tarieven,
            // een open factuur, een betaalde creditnota en een concept.
            cmd.CommandText = """
                INSERT INTO Workspaces (Id, CreatedAt, EmailsSent, WebhooksSent, Name, Plan) VALUES (1, '2026-01-01 00:00:00', 0, 0, 'Oud bedrijf', 'team');
                INSERT INTO Customers (Id, WorkspaceId, CreatedAt, Name, Country, RemindersOff, IsDemo) VALUES (1, 1, '2026-01-01 00:00:00', 'Jan', 'Nederland', 0, 0);
                INSERT INTO Invoices (Id, WorkspaceId, CustomerId, Number, Status, IssueDate, DueDate, PaidAt, VatRegime, IsDemo, RemindersOff, HourSpecification, HourAmounts) VALUES
                    (1, 1, 1, '2026-0001', 'betaald',   '2026-09-01 00:00:00', '2026-09-15 00:00:00', '2026-09-20 00:00:00', 'normaal', 0, 0, 1, 0),
                    (2, 1, 1, '2026-0002', 'verzonden', '2026-09-05 00:00:00', '2026-12-19 00:00:00', NULL, 'normaal', 0, 0, 1, 0),
                    (3, 1, 1, '2026-0003', 'betaald',   '2026-09-10 00:00:00', '2026-09-24 00:00:00', '2026-09-12 00:00:00', 'normaal', 0, 0, 1, 0),
                    (4, 1, 1, NULL,        'concept',   '2026-09-11 00:00:00', '2026-09-25 00:00:00', NULL, 'normaal', 0, 0, 1, 0);
                UPDATE Invoices SET CreditForInvoiceId = 2 WHERE Id = 3;
                INSERT INTO InvoiceLines (Id, InvoiceId, Position, Description, Quantity, Unit, UnitPrice, VatRate) VALUES
                    (1, 1, 0, 'Werk', 3, 'uur', 33.335, 21),
                    (2, 1, 1, 'Boek', 0.5, 'stuk', 1.0, 21),
                    (3, 1, 2, 'Kopjes', 2.5, 'stuk', 10.01, 9),
                    (4, 2, 0, 'Werk', 1, 'stuk', 100, 21),
                    (5, 3, 0, 'Werk', -1, 'stuk', 10, 21),
                    (6, 4, 0, 'Werk', 1, 'stuk', 100, 21);
                """;
            cmd.ExecuteNonQuery();
        }
        using (var db = Db()) db.Database.Migrate();

        using (var db = Db(1))
        {
            var invoices = db.Invoices.Include(i => i.Lines).Include(i => i.Credits).ThenInclude(c => c.Lines).Include(i => i.Payments).AsSplitQuery()
                .OrderBy(i => i.Id).ToList();
            var paid = invoices[0];
            Assert.Equal(InvoiceStatus.Paid, paid.Status);
            Assert.Equal(new DateTime(2026, 9, 20), paid.PaidAt);
            Assert.Equal(0m, paid.OpenAmount);
            var payment = Assert.Single(paid.Payments);
            // Precies het totaal zoals Money.Totals het uitrekent: (100,01 + 0,50) × 1,21 afgerond per tarief, plus 25,03 met 9%.
            Assert.Equal(paid.Totals.Total, payment.Amount);
            Assert.Equal(121.62m + 27.28m, payment.Amount);
            Assert.Equal(new DateTime(2026, 9, 20), payment.Date);
            Assert.Equal(PaymentMethods.Manual, payment.Method);
            // De open factuur, de creditnota en het concept krijgen geen betaling; wat er openstaat blijft gelijk.
            Assert.Empty(invoices[1].Payments);
            Assert.Equal(121m - 12.10m, invoices[1].OpenAmount);
            Assert.Empty(invoices[2].Payments);
            Assert.Empty(invoices[3].Payments);
        }
    }

    public void Dispose()
    {
        TestDatabase.Delete(path);
    }
}

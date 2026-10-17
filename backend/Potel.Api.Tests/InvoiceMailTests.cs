using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Invoicing;
using Potel.Api.Workflows;
using static Potel.Api.Tests.InvoiceKit;

namespace Potel.Api.Tests;

// De wettelijke regels voor herinneringen en aanmaningen, los van de rest.
public class ReminderRuleTests
{
    [Theory]
    [InlineData(100, 40)]
    [InlineData(266, 40)]
    [InlineData(300, 45)]
    [InlineData(2500, 375)]
    [InlineData(3000, 425)]
    [InlineData(5000, 625)]
    [InlineData(10000, 875)]
    [InlineData(200000, 2775)]
    [InlineData(2000000, 6775)]
    public void Collection_costs_follow_the_statutory_scale(decimal open, decimal costs) =>
        Assert.Equal(costs, ReminderRules.CollectionCosts(open));

    [Fact]
    public void A_consumer_gets_fourteen_days_and_costs_only_as_a_next_step()
    {
        var text = MailTemplates.Consequences(consumer: true, 120m, new DateTime(2026, 11, 1));
        Assert.Contains("binnen 14 dagen na ontvangst", text);
        Assert.Contains("uiterlijk op 16 november 2026", text);
        Assert.Contains("Betaal je niet op tijd, dan moeten we incassokosten in rekening brengen", text);
        Assert.Contains("€ 40,00", text);
        Assert.DoesNotContain("rente", text);
    }

    [Fact]
    public void A_business_hears_about_commercial_interest_and_collection_costs()
    {
        var text = MailTemplates.Consequences(consumer: false, 3000m, new DateTime(2026, 11, 1));
        Assert.Contains("wettelijke handelsrente", text);
        Assert.Contains("€ 425,00", text);
    }

    [Fact]
    public void Costs_and_interest_only_belong_in_the_final_notice()
    {
        Assert.Null(MailTemplates.Problem(MailSteps.First, MailTemplates.First.Subject, MailTemplates.First.Body));
        Assert.Null(MailTemplates.Problem(MailSteps.Final, MailTemplates.Final.Subject, MailTemplates.Final.Body));
        Assert.Contains("alleen in de aanmaning", MailTemplates.Problem(MailSteps.First, "Herinnering", "We rekenen incassokosten.")!);
        Assert.Contains("alleen in de aanmaning", MailTemplates.Problem(MailSteps.Second, "Herinnering", "Zie {gevolgen}")!);
        Assert.Contains("{gevolgen}", MailTemplates.Problem(MailSteps.Final, "Aanmaning", "Betaal nu.")!);
        // De standaardteksten van de herinneringen noemen geen kosten of rente.
        foreach (var t in new[] { MailTemplates.First, MailTemplates.Second })
            Assert.DoesNotMatch("incassokosten|rente", t.Body);
    }
}

// Een mailserver voor tests: houdt bij wat er weg gaat, en kan doen alsof hij er niet is of een fout geeft.
public sealed class TestMailServer : IEmailSender
{
    public readonly ConcurrentQueue<OutgoingEmail> Sent = new();
    public bool Configured { get; set; } = true;
    public bool Fail { get; set; }

    public Task SendAsync(OutgoingEmail mail, CancellationToken ct = default)
    {
        if (Fail) throw new InvalidOperationException("Mailserver onbereikbaar");
        Sent.Enqueue(mail);
        return Task.CompletedTask;
    }

    public List<OutgoingEmail> To(string address) => Sent.Where(m => m.To == address).ToList();
}

public class MailFactory : PortalFactory
{
    public MovableClock Clock { get; } = new(new DateTime(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc));
    public TestMailServer Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("App:BaseUrl", "https://potel.test");
        builder.UseSetting("Invoicing:StartHour", "0");
        builder.UseSetting("Invoicing:CheckEveryMinutes", "0");
        builder.UseSetting("Workflows:EmailsPerDayTrial", "50");
        builder.UseSetting("Workflows:EmailsPerDayAllTrials", "100000");
        builder.ConfigureTestServices(s =>
        {
            s.AddSingleton<BusinessClock>(Clock);
            s.AddSingleton<IEmailSender>(Mail);
        });
    }
}

// Facturen en herinneringen per e-mail: alleen naar de klant, binnen de daglimiet, elke stap één keer, en een veilige link.
public class InvoiceMailTests(MailFactory factory) : IClassFixture<MailFactory>
{
    static DateTime D(int y, int m, int d) => new(y, m, d);
    static readonly Regex TokenInLink = new(@"https://potel\.test/factuur#token=([A-Za-z0-9_-]+)");

    static object Reminders(bool enabled = true, int first = 7, int second = 14, int final = 28) =>
        new { enabled, firstDays = first, secondDays = second, finalDays = final };

    async Task<(HttpClient Client, int Workspace, int Customer, string Email)> ReadyAsync(string prefix, bool business = true, string? email = null, bool reminders = true)
    {
        factory.Clock.SetDay(2026, 10, 12);
        var c = await InvoiceKit.ReadyAsync(factory, prefix);
        email ??= $"klant-{Guid.NewGuid():N}@example.com";
        var res = await c.PostAsJsonAsync("/api/customers", new
        {
            name = "Sanne de Vries", company = business ? "Bakkerij De Vries" : null, email, address = "Dorpsstraat 1", city = "1234 AB Utrecht",
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        if (reminders) (await c.PutAsJsonAsync("/api/reminders", Reminders())).EnsureSuccessStatusCode();
        return (c, await TestApi.WorkspaceIdAsync(c), (await res.JsonAsync()).GetProperty("id").GetInt32(), email);
    }

    // Een verstuurde factuur van vandaag (12 oktober), die op 26 oktober vervalt.
    static Task<int> SentInvoiceAsync(HttpClient c, int customer, decimal price = 100) => InvoiceAsync(c, customer, "verzonden", 1, price);

    async Task<int> RunRemindersAsync(int workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = workspaceId;
        return await scope.ServiceProvider.GetRequiredService<ReminderService>().RunAsync();
    }

    List<InvoiceMail> Mails(int ws, int invoiceId) =>
        WorkflowKit.InWorkspace(factory, ws, db => db.InvoiceMails.Where(m => m.InvoiceId == invoiceId).OrderBy(m => m.Id).ToList());

    List<string> Log(int ws) => WorkflowKit.InWorkspace(factory, ws, db => db.Activities.Select(a => a.Text).ToList());

    static string Token(OutgoingEmail mail) => TokenInLink.Match(mail.Body) is { Success: true } m ? m.Groups[1].Value : throw new Exception("Geen link in de mail");

    [Fact]
    public async Task Reminders_follow_the_steps_after_the_due_date_once_each()
    {
        var (c, ws, customer, email) = await ReadyAsync("herinnering-stappen");
        var id = await SentInvoiceAsync(c, customer);
        var number = (await GetInvoiceAsync(c, id)).GetProperty("number").GetString();

        factory.Clock.SetDay(2026, 11, 1);
        Assert.Equal(0, await RunRemindersAsync(ws));
        factory.Clock.SetDay(2026, 11, 2);
        Assert.Equal(1, await RunRemindersAsync(ws));
        Assert.Equal(0, await RunRemindersAsync(ws));
        var first = Assert.Single(factory.Mail.To(email));
        Assert.Equal($"Herinnering: factuur {number}", first.Subject);
        Assert.Contains("€ 121,00", first.Body);
        Assert.Contains("26 oktober 2026", first.Body);
        Assert.Contains("NL00 BANK 0123 4567 89", first.Body);
        Assert.Matches(TokenInLink, first.Body);
        Assert.DoesNotMatch("incassokosten|rente", first.Body);
        Assert.Equal("Mijn Bedrijf", first.FromName);
        Assert.Equal("hallo@mijnbedrijf.nl", first.ReplyTo);

        factory.Clock.SetDay(2026, 11, 8);
        Assert.Equal(0, await RunRemindersAsync(ws));
        factory.Clock.SetDay(2026, 11, 9);
        Assert.Equal(1, await RunRemindersAsync(ws));
        Assert.StartsWith("Tweede herinnering", factory.Mail.To(email)[1].Subject);
        factory.Clock.SetDay(2026, 11, 23);
        Assert.Equal(1, await RunRemindersAsync(ws));
        var final = factory.Mail.To(email)[2];
        Assert.StartsWith("Aanmaning", final.Subject);
        // Een bedrijf: wettelijke handelsrente en incassokosten, pas in de aanmaning.
        Assert.Contains("wettelijke handelsrente", final.Body);
        Assert.Contains("€ 40,00", final.Body);
        factory.Clock.SetDay(2027, 3, 1);
        Assert.Equal(0, await RunRemindersAsync(ws));

        var log = Mails(ws, id);
        Assert.Equal([1, 2, 3], log.Select(m => m.Step));
        Assert.All(log, m => Assert.True(m is { Automatic: true, Status: MailStatus.Sent } && m.To == email && m.SentAt != null));
        var mails = await c.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}/mails");
        Assert.Equal(3, mails.GetProperty("mails").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, mails.GetProperty("nextReminder").ValueKind);
    }

    [Fact]
    public async Task A_consumer_gets_the_fourteen_day_notice()
    {
        var (c, ws, customer, email) = await ReadyAsync("herinnering-particulier", business: false);
        var id = await SentInvoiceAsync(c, customer);
        foreach (var day in new[] { 2, 9, 23 })
        {
            factory.Clock.SetDay(2026, 11, day);
            Assert.Equal(1, await RunRemindersAsync(ws));
        }
        var final = factory.Mail.To(email)[2];
        Assert.Contains("binnen 14 dagen na ontvangst van deze aanmaning", final.Body);
        Assert.Contains("uiterlijk op 8 december 2026", final.Body);
        Assert.Contains("dan moeten we incassokosten in rekening brengen", final.Body);
        Assert.DoesNotContain("handelsrente", final.Body);
        Assert.True((await c.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}/mail-preview?step=3")).GetProperty("consumer").GetBoolean());
    }

    [Fact]
    public async Task Turning_reminders_on_late_starts_friendly_and_keeps_the_steps_apart()
    {
        var (c, ws, customer, email) = await ReadyAsync("herinnering-laat", reminders: false);
        await SentInvoiceAsync(c, customer);
        factory.Clock.SetDay(2027, 1, 10);
        Assert.Equal(0, await RunRemindersAsync(ws));
        (await c.PutAsJsonAsync("/api/reminders", Reminders())).EnsureSuccessStatusCode();
        Assert.Equal(1, await RunRemindersAsync(ws));
        Assert.StartsWith("Herinnering", Assert.Single(factory.Mail.To(email)).Subject);
        factory.Clock.SetDay(2027, 1, 16);
        Assert.Equal(0, await RunRemindersAsync(ws));
        factory.Clock.SetDay(2027, 1, 17);
        Assert.Equal(1, await RunRemindersAsync(ws));
        factory.Clock.SetDay(2027, 1, 30);
        Assert.Equal(0, await RunRemindersAsync(ws));
        factory.Clock.SetDay(2027, 1, 31);
        Assert.Equal(1, await RunRemindersAsync(ws));
    }

    [Fact]
    public async Task Partly_credited_invoices_remind_the_open_amount_and_paid_credited_or_drafts_never()
    {
        var (c, ws, customer, email) = await ReadyAsync("herinnering-credit");
        var partly = await SentInvoiceAsync(c, customer, 1000);
        var credit = (await (await c.PostAsync($"/api/invoices/{partly}/credit", null)).JsonAsync()).GetProperty("id").GetInt32();
        var draft = await GetInvoiceAsync(c, credit);
        (await c.PutAsJsonAsync($"/api/invoices/{credit}", WithLines(draft, new[] { new { description = "Deels", quantity = -1, unit = "stuk", unitPrice = 400, vatRate = 21 } }))).EnsureSuccessStatusCode();
        (await SetStatusAsync(c, credit, "verzonden")).EnsureSuccessStatusCode();

        var full = await SentInvoiceAsync(c, customer, 200);
        (await SetStatusAsync(c, (await (await c.PostAsync($"/api/invoices/{full}/credit", null)).JsonAsync()).GetProperty("id").GetInt32(), "verzonden")).EnsureSuccessStatusCode();
        var paid = await SentInvoiceAsync(c, customer, 300);
        (await SetStatusAsync(c, paid, "betaald")).EnsureSuccessStatusCode();
        var concept = await InvoiceAsync(c, customer, "concept", 1, 400);

        factory.Clock.SetDay(2026, 11, 2);
        Assert.Equal(1, await RunRemindersAsync(ws));
        var mail = Assert.Single(factory.Mail.To(email));
        // 1.210 min 484 gecrediteerd.
        Assert.Contains("Er staat nog € 726,00 open", mail.Body);
        Assert.Empty(Mails(ws, full));
        Assert.Empty(Mails(ws, paid));
        Assert.Empty(Mails(ws, concept));
        Assert.Empty(Mails(ws, credit));

        // Betaalt de klant, dan stopt het.
        (await SetStatusAsync(c, partly, "betaald")).EnsureSuccessStatusCode();
        factory.Clock.SetDay(2026, 11, 20);
        Assert.Equal(0, await RunRemindersAsync(ws));
    }

    [Fact]
    public async Task Reminders_can_be_turned_off_per_invoice_and_per_customer()
    {
        var (c, ws, customer, email) = await ReadyAsync("herinnering-uit");
        var off = await SentInvoiceAsync(c, customer);
        Assert.True((await (await c.PostAsJsonAsync($"/api/invoices/{off}/reminders", new { off = true })).JsonAsync()).GetProperty("remindersOff").GetBoolean());
        var on = await SentInvoiceAsync(c, customer);
        factory.Clock.SetDay(2026, 11, 2);
        Assert.Equal(1, await RunRemindersAsync(ws));
        Assert.Empty(Mails(ws, off));
        Assert.Single(Mails(ws, on));

        var cust = await c.GetFromJsonAsync<JsonElement>($"/api/customers/{customer}");
        var res = await c.PutAsJsonAsync($"/api/customers/{customer}", new
        {
            name = cust.GetProperty("name").GetString(), company = "Bakkerij De Vries", email, address = "Dorpsstraat 1", city = "1234 AB Utrecht", remindersOff = true,
        });
        Assert.True((await res.JsonAsync()).GetProperty("remindersOff").GetBoolean());
        factory.Clock.SetDay(2026, 11, 20);
        Assert.Equal(0, await RunRemindersAsync(ws));
        Assert.Single(Mails(ws, on));
    }

    [Fact]
    public async Task Without_a_mail_server_or_quota_nothing_goes_and_the_log_says_why_once_a_day()
    {
        var (c, ws, customer, email) = await ReadyAsync("herinnering-geen-server");
        var id = await SentInvoiceAsync(c, customer);
        factory.Clock.SetDay(2026, 11, 2);
        factory.Mail.Configured = false;
        try
        {
            Assert.Equal(0, await RunRemindersAsync(ws));
            Assert.Equal(0, await RunRemindersAsync(ws));
            Assert.Single(Log(ws), l => l.Contains("Automatische herinneringen niet verstuurd: Er is nog geen mailserver ingesteld") && l.Contains("Er wacht 1 factuur op een herinnering"));
            var preview = await c.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}/mail-preview?step=1");
            Assert.Equal(InvoiceMailer.NoServer, preview.GetProperty("problem").GetString());
            var manual = await c.PostAsJsonAsync($"/api/invoices/{id}/mail", new { step = 1 });
            Assert.Equal(HttpStatusCode.BadRequest, manual.StatusCode);
            Assert.False((await c.GetFromJsonAsync<JsonElement>("/api/invoices/mail-status")).GetProperty("canSend").GetBoolean());
        }
        finally
        {
            factory.Mail.Configured = true;
        }
        Assert.Empty(factory.Mail.To(email));

        // De daglimiet van de werkruimte is op: ook dan niets, met een duidelijke regel.
        WorkflowKit.InWorkspace(factory, ws, db =>
        {
            var w = db.Workspaces.Single(x => x.Id == ws);
            w.EmailDay = DateTime.UtcNow.Date;
            w.EmailsSent = 50;
            return db.SaveChanges();
        });
        Assert.Equal(0, await RunRemindersAsync(ws));
        Assert.Contains(Log(ws), l => l.Contains("daglimiet van 50 e-mails"));
        Assert.Empty(Mails(ws, id).Where(m => m.Status == MailStatus.Sent));
        var refused = await c.PostAsJsonAsync($"/api/invoices/{id}/mail", new { step = 0 });
        Assert.Contains("daglimiet", await refused.ErrorAsync());
    }

    [Fact]
    public async Task A_mail_server_error_is_logged_and_tried_again_later()
    {
        var (c, ws, customer, email) = await ReadyAsync("herinnering-fout");
        var id = await SentInvoiceAsync(c, customer);
        factory.Clock.SetDay(2026, 11, 2);
        factory.Mail.Fail = true;
        try
        {
            Assert.Equal(0, await RunRemindersAsync(ws));
            // Niet meteen elke ronde opnieuw.
            Assert.Equal(0, await RunRemindersAsync(ws));
        }
        finally
        {
            factory.Mail.Fail = false;
        }
        var failed = Assert.Single(Mails(ws, id));
        Assert.Equal(MailStatus.Failed, failed.Status);
        Assert.Contains(Log(ws), l => l.Contains("kon niet worden verstuurd"));
        // De link uit de mislukte mail is meteen ingetrokken.
        Assert.All(WorkflowKit.InWorkspace(factory, ws, db => db.InvoiceLinks.Where(l => l.InvoiceId == id).ToList()), l => Assert.NotNull(l.RevokedAt));
        factory.Clock.Value = factory.Clock.Value.AddHours(7);
        Assert.Equal(1, await RunRemindersAsync(ws));
        Assert.Single(factory.Mail.To(email));
    }

    [Fact]
    public async Task A_customer_without_a_valid_address_is_skipped_and_noted_once()
    {
        var (c, ws, customer, _) = await ReadyAsync("herinnering-adres", email: "een@example.com, twee@example.com");
        var id = await SentInvoiceAsync(c, customer);
        factory.Clock.SetDay(2026, 11, 2);
        Assert.Equal(0, await RunRemindersAsync(ws));
        Assert.Equal(0, await RunRemindersAsync(ws));
        var skipped = Assert.Single(Mails(ws, id));
        Assert.Equal(MailStatus.Skipped, skipped.Status);
        Assert.Equal(InvoiceMailer.NoEmail, skipped.Note);
        Assert.Single(Log(ws), l => l.Contains("niet verstuurd: Deze klant heeft geen geldig e-mailadres"));
        Assert.Empty(factory.Mail.Sent.Where(m => m.To.Contains("twee@example.com")));
    }

    [Fact]
    public async Task Concurrent_runs_never_send_the_same_step_twice()
    {
        var (c, ws, customer, email) = await ReadyAsync("herinnering-tegelijk");
        var id = await SentInvoiceAsync(c, customer);
        factory.Clock.SetDay(2026, 11, 2);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => RunRemindersAsync(ws))));
        Assert.Equal(1, results.Sum());
        Assert.Single(factory.Mail.To(email));
        Assert.Single(Mails(ws, id));

        // En de database zelf weigert een tweede automatische mail voor dezelfde stap.
        Assert.Throws<DbUpdateException>(() => WorkflowKit.InWorkspace(factory, ws, db =>
        {
            db.InvoiceMails.Add(new InvoiceMail { InvoiceId = id, Step = 1, To = email, Status = MailStatus.Sent, Automatic = true });
            return db.SaveChanges();
        }));
    }

    [Fact]
    public async Task Mailing_an_invoice_by_hand_goes_to_the_customer_with_a_link_and_counts_for_the_reminders()
    {
        var (c, ws, customer, email) = await ReadyAsync("factuur-mailen");
        var concept = await InvoiceAsync(c, customer, "concept", 1, 100);
        Assert.Contains("Verstuur de factuur eerst", await (await c.PostAsJsonAsync($"/api/invoices/{concept}/mail", new { step = 0 })).ErrorAsync());

        var id = await SentInvoiceAsync(c, customer);
        var preview = await c.GetFromJsonAsync<JsonElement>($"/api/invoices/{id}/mail-preview?step=0");
        Assert.Equal(email, preview.GetProperty("to").GetString());
        Assert.Contains("{link}", preview.GetProperty("body").GetString());
        Assert.Equal(JsonValueKind.Null, preview.GetProperty("problem").ValueKind);

        var res = await c.PostAsJsonAsync($"/api/invoices/{id}/mail", new { step = 0, subject = "Je factuur", body = "Hoi Sanne,\n\nHier is hij: {link}\n\nGroet" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var mail = Assert.Single(factory.Mail.To(email));
        Assert.Equal("Je factuur", mail.Subject);
        Assert.Matches(TokenInLink, mail.Body);
        var logged = Assert.Single(Mails(ws, id));
        Assert.Equal((0, false, "Eigenaar"), (logged.Step, logged.Automatic, logged.SentBy));

        // Een herinnering kan pas na de vervaldatum.
        Assert.Contains("nog niet te laat", await (await c.PostAsJsonAsync($"/api/invoices/{id}/mail", new { step = 1 })).ErrorAsync());
        factory.Clock.SetDay(2026, 10, 30);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/invoices/{id}/mail", new { step = 1 })).StatusCode);
        // De automatische ronde stuurt herinnering 1 niet nog eens, maar gaat verder met de tweede als die aan de beurt is.
        factory.Clock.SetDay(2026, 11, 2);
        Assert.Equal(0, await RunRemindersAsync(ws));
        factory.Clock.SetDay(2026, 11, 9);
        Assert.Equal(1, await RunRemindersAsync(ws));
        Assert.Equal([0, 1, 2], Mails(ws, id).Select(m => m.Step));
        // Een te lange tekst gaat niet weg.
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync($"/api/invoices/{id}/mail", new { step = 0, body = new string('a', 6000) })).StatusCode);
    }

    [Fact]
    public async Task The_public_link_shows_only_that_invoice_stores_only_a_hash_and_can_be_revoked()
    {
        var (c, ws, customer, email) = await ReadyAsync("factuur-link");
        var id = await SentInvoiceAsync(c, customer);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/api/invoices/{id}/mail", new { step = 0 })).StatusCode);
        var token = Token(Assert.Single(factory.Mail.To(email)));
        Assert.True(token.Length >= 43, "Het token heeft minstens 32 willekeurige bytes");

        var stored = Assert.Single(WorkflowKit.InWorkspace(factory, ws, db => db.InvoiceLinks.Where(l => l.InvoiceId == id).ToList()));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), stored.TokenHash);
        Assert.DoesNotContain(token, stored.TokenHash);
        Assert.DoesNotContain(WorkflowKit.InWorkspace(factory, ws, db => db.InvoiceMails.Select(m => m.Subject + m.Note).ToList()), s => s.Contains(token));

        // Zonder inloggen.
        var anonymous = factory.CreateClient();
        var res = await anonymous.PostAsJsonAsync("/api/public/invoice", new { token });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("noindex", res.Headers.GetValues("X-Robots-Tag").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        var body = await res.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal((await GetInvoiceAsync(c, id)).GetProperty("number").GetString(), json.GetProperty("invoice").GetProperty("number").GetString());
        Assert.Equal("Mijn Bedrijf", json.GetProperty("invoice").GetProperty("seller").GetProperty("name").GetString());
        // Niets anders uit de werkruimte: geen e-mailadres van de klant, geen andere facturen of klanten.
        Assert.DoesNotContain(email, body);
        Assert.DoesNotContain("customerId", body);
        Assert.DoesNotContain("creditForInvoiceId", body);
        Assert.DoesNotContain("workspaceId", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, WorkflowKit.InWorkspace(factory, ws, db => db.InvoiceLinks.Single(l => l.InvoiceId == id).Opens));

        // De pagina zelf hoort ook niet in zoekmachines.
        var page = await anonymous.GetAsync("/factuur");
        Assert.Contains("noindex", page.Headers.GetValues("X-Robots-Tag").Single());

        // Een verzonnen of net iets ander token werkt niet, en zegt niet of het bestaat.
        var wrong = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');
        foreach (var t in new[] { wrong, "", "x", new string('a', 500), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))) })
        {
            var r = await anonymous.PostAsJsonAsync("/api/public/invoice", new { token = t });
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
            Assert.Equal(Potel.Api.Endpoints.InvoiceMailEndpoints.LinkGone, await r.ErrorAsync());
        }

        // Een andere werkruimte kan de link niet intrekken of de factuur mailen.
        var (other, _, _, _) = await ReadyAsync("factuur-link-ander");
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/invoices/{id}/links/revoke", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/invoices/{id}/mail", new { step = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/invoices/{id}/mail-preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/invoices/{id}/mails")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync($"/api/invoices/{id}/reminders", new { off = true })).StatusCode);
        // Ingelogd in een andere werkruimte werkt de openbare link gewoon, maar toont nog steeds alleen deze factuur.
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync("/api/public/invoice", new { token })).StatusCode);

        var revoked = await (await c.PostAsync($"/api/invoices/{id}/links/revoke", null)).JsonAsync();
        Assert.Equal(1, revoked.GetProperty("revoked").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PostAsJsonAsync("/api/public/invoice", new { token })).StatusCode);
    }

    [Fact]
    public async Task Demo_invoices_are_never_mailed()
    {
        factory.Clock.SetDay(2026, 10, 12);
        var c = await TestApi.RegisterAsync(factory, $"mail-demo-{Guid.NewGuid():N}@example.com");
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync("/api/workspace/demo-data", null)).StatusCode);
        (await c.PutAsJsonAsync("/api/settings", Company())).EnsureSuccessStatusCode();
        (await c.PutAsJsonAsync("/api/reminders", Reminders())).EnsureSuccessStatusCode();
        var ws = await TestApi.WorkspaceIdAsync(c);
        var demo = (await c.GetFromJsonAsync<List<JsonElement>>("/api/invoices"))!.First(i => i.GetProperty("demo").GetBoolean() && i.GetProperty("status").GetString() != "concept");
        Assert.Contains("voorbeeldfactuur", await (await c.PostAsJsonAsync($"/api/invoices/{demo.GetProperty("id").GetInt32()}/mail", new { step = 0 })).ErrorAsync());
        factory.Clock.SetDay(2027, 6, 1);
        var before = factory.Mail.Sent.Count;
        Assert.Equal(0, await RunRemindersAsync(ws));
        Assert.Equal(before, factory.Mail.Sent.Count);
    }

    [Fact]
    public async Task Employees_can_mail_but_only_admins_change_reminder_settings_and_read_only_workspaces_send_nothing()
    {
        var (admin, ws, customer, email) = await ReadyAsync("herinnering-rol");
        var id = await SentInvoiceAsync(admin, customer);
        var employeeEmail = $"medewerker-{Guid.NewGuid():N}@example.com";
        await TestApi.CreateUserAsync(admin, employeeEmail);
        var employee = await TestApi.LoginAsync(factory, employeeEmail);
        Assert.Equal(HttpStatusCode.OK, (await employee.GetAsync("/api/reminders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await employee.PutAsJsonAsync("/api/reminders", Reminders(false))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await employee.PostAsJsonAsync($"/api/invoices/{id}/mail", new { step = 0 })).StatusCode);

        await TestApi.ExpireTrialAsync(factory, ws);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.PostAsJsonAsync($"/api/invoices/{id}/mail", new { step = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await admin.PutAsJsonAsync("/api/reminders", Reminders())).StatusCode);
        factory.Clock.SetDay(2026, 11, 2);
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = ws;
            foreach (var job in scope.ServiceProvider.GetServices<IWorkspaceJob>()) await job.RunAsync(CancellationToken.None);
        }
        Assert.Single(factory.Mail.To(email));
        // De openbare link werkt ook voor een werkruimte die alleen-lezen is.
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().PostAsJsonAsync("/api/public/invoice", new { token = Token(factory.Mail.To(email)[0]) })).StatusCode);
    }

    [Fact]
    public async Task Unverified_admins_cannot_send_mail()
    {
        factory.Clock.SetDay(2026, 10, 12);
        var c = await TestApi.RegisterAsync(factory, $"onbevestigd-{Guid.NewGuid():N}@example.com", verified: false);
        (await c.PutAsJsonAsync("/api/settings", Company())).EnsureSuccessStatusCode();
        var res = await c.PostAsJsonAsync("/api/customers", new { name = "Klant", company = "Klant B.V.", email = "klant-onbevestigd@example.com", address = "Straat 1", city = "1234 AB Utrecht" });
        var id = await SentInvoiceAsync(c, (await res.JsonAsync()).GetProperty("id").GetInt32());
        var refused = await c.PostAsJsonAsync($"/api/invoices/{id}/mail", new { step = 0 });
        Assert.Equal(InvoiceMailer.NotVerified, await refused.ErrorAsync());
        Assert.Empty(factory.Mail.To("klant-onbevestigd@example.com"));
    }

    [Fact]
    public async Task Reminder_texts_are_checked_and_defaults_are_kept_as_defaults()
    {
        var (c, ws, _, _) = await ReadyAsync("herinnering-teksten");
        var bad = await c.PutAsJsonAsync("/api/reminders", new { enabled = true, firstDays = 7, secondDays = 14, finalDays = 28, first = new { subject = "Herinnering", body = "Wij rekenen incassokosten." } });
        Assert.Contains("alleen in de aanmaning", await bad.ErrorAsync());
        var noConsequences = await c.PutAsJsonAsync("/api/reminders", new { enabled = true, firstDays = 7, secondDays = 14, finalDays = 28, final = new { subject = "Aanmaning", body = "Betaal nu." } });
        Assert.Contains("{gevolgen}", await noConsequences.ErrorAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/reminders", Reminders(first: 14, second: 7))).StatusCode);

        var ok = await c.PutAsJsonAsync("/api/reminders", new
        {
            enabled = true, firstDays = 5, secondDays = 12, finalDays = 30,
            first = new { subject = "Even een seintje over {nummer}", body = MailTemplates.First.Body },
        });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var saved = WorkflowKit.InWorkspace(factory, ws, db => db.ReminderSettings.Single());
        Assert.Equal("Even een seintje over {nummer}", saved.FirstSubject);
        // Gelijk aan de standaardtekst: blijft leeg, zodat een betere standaardtekst later vanzelf meekomt.
        Assert.Null(saved.FirstBody);
        Assert.Equal((5, 12, 30), (saved.FirstDays, saved.SecondDays, saved.FinalDays));
    }

    [Fact]
    public async Task The_overdue_trigger_starts_workflows_once()
    {
        var (c, ws, customer, _) = await ReadyAsync("factuur-te-laat", reminders: false);
        var graph = WorkflowKit.GraphJson(
            [WorkflowKit.Node("s", "trigger.overdue"), WorkflowKit.Node("t", "action.task", new() { ["title"] = "Bellen over {{invoice.number}} ({{invoice.open}})", ["days"] = "0" })],
            [WorkflowKit.Edge("s", "t")]);
        await WorkflowKit.CreateWorkflowAsync(c, "Te laat", graph, active: true);
        var id = await SentInvoiceAsync(c, customer);
        var number = (await GetInvoiceAsync(c, id)).GetProperty("number").GetString();
        factory.Clock.SetDay(2026, 10, 27);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => c.GetAsync("/api/invoices")));
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = ws;
            foreach (var job in scope.ServiceProvider.GetServices<IWorkspaceJob>()) await job.RunAsync(CancellationToken.None);
        }
        var tasks = WorkflowKit.InWorkspace(factory, ws, db => db.Appointments.Where(a => a.Title.StartsWith("Bellen over")).Select(a => a.Title).ToList());
        Assert.Equal([$"Bellen over {number} (121.00)"], tasks);
        Assert.Equal("verlopen", (await GetInvoiceAsync(c, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_recurring_invoice_can_be_sent_straight_away_or_falls_back_to_a_draft()
    {
        var (c, ws, customer, email) = await ReadyAsync("terugkerend-versturen");
        object Template(int customerId) => new
        {
            name = "Hosting", customerId, vatRegime = "normaal", interval = "maand", periodRule = "vooraf", action = "versturen", startDate = "2026-10-01",
            lines = new[] { new { description = "Hosting {maand} {jaar}", quantity = 1, unit = "maand", unitPrice = 50, vatRate = 21 } },
        };
        var res = await c.PostAsJsonAsync("/api/recurring-invoices", Template(customer));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var recurringId = (await res.JsonAsync()).GetProperty("id").GetInt32();
        var made = Assert.Single(WorkflowKit.InWorkspace(factory, ws, db => db.Invoices.Where(i => i.RecurringInvoiceId == recurringId).ToList()));
        Assert.Equal("verzonden", made.Status);
        Assert.NotNull(made.Number);
        var mail = Assert.Single(factory.Mail.To(email));
        Assert.Contains("Hosting", (await GetInvoiceAsync(c, made.Id)).GetProperty("lines")[0].GetProperty("description").GetString());
        Assert.Matches(TokenInLink, mail.Body);

        // Zonder e-mailadres bij de klant blijft hij een concept, met de reden in het logboek.
        var noMail = (await (await c.PostAsJsonAsync("/api/customers", new { name = "Zonder mail", company = "Zonder B.V.", address = "Straat 2", city = "1234 AB Utrecht" })).JsonAsync()).GetProperty("id").GetInt32();
        var second = await (await c.PostAsJsonAsync("/api/recurring-invoices", Template(noMail))).JsonAsync();
        var draft = Assert.Single(WorkflowKit.InWorkspace(factory, ws, db => db.Invoices.Where(i => i.RecurringInvoiceId == second.GetProperty("id").GetInt32()).ToList()));
        Assert.Equal("concept", draft.Status);
        Assert.Contains(Log(ws), l => l.Contains("is niet verstuurd. Deze klant heeft geen geldig e-mailadres"));

        // Zonder mailserver kun je "direct versturen" niet kiezen.
        factory.Mail.Configured = false;
        try
        {
            var refused = await c.PostAsJsonAsync("/api/recurring-invoices", Template(customer));
            Assert.Equal(Potel.Api.Endpoints.RecurringInvoiceEndpoints.SendNotPossible, await refused.ErrorAsync());
        }
        finally
        {
            factory.Mail.Configured = true;
        }
    }
}

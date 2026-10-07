using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Workflows;
using static Potel.Api.Tests.TestApi;
using static Potel.Api.Tests.WorkflowKit;

namespace Potel.Api.Tests;

// Een werkruimte met een lage daglimiet tijdens de proef en een hogere met abonnement.
public class RecordingMailFactory : PortalFactory
{
    public RecordingEmailSender Mail { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Workflows:EmailsPerDayTrial", "3");
        builder.UseSetting("Workflows:EmailsPerDayPaid", "6");
        builder.ConfigureTestServices(s => s.AddSingleton<IEmailSender>(Mail));
    }
}

// Het e-mailblok gaat via de mailserver van het platform, dus het mag geen spamkanon worden.
public class WorkflowEmailTests(RecordingMailFactory factory) : IClassFixture<RecordingMailFactory>
{
    static object Mail(string id, string to, string subject = "Hallo") =>
        Node(id, "action.email", new() { ["to"] = to, ["subject"] = subject, ["body"] = "Beste klant," });

    static string Chain(int mails, string to)
    {
        var nodes = new List<object> { Node("s", "trigger.manual") };
        var edges = new List<object>();
        for (var i = 1; i <= mails; i++)
        {
            nodes.Add(Mail($"m{i}", to));
            edges.Add(Edge(i == 1 ? "s" : $"m{i - 1}", $"m{i}"));
        }
        nodes.Add(Node("t", "action.task", new() { ["title"] = "Na de mails", ["days"] = "0" }));
        edges.Add(Edge($"m{mails}", "t"));
        return GraphJson([.. nodes], [.. edges]);
    }

    [Theory]
    [InlineData("v1@slachtoffer.example, v2@slachtoffer.example")]
    [InlineData("v1@slachtoffer.example; v2@slachtoffer.example")]
    [InlineData("Bank <v1@slachtoffer.example>")]
    [InlineData("v1@slachtoffer.example\r\nBcc: v2@slachtoffer.example")]
    [InlineData("geen-adres")]
    public async Task Email_block_sends_to_exactly_one_valid_address(string to)
    {
        var tenant = await RegisterAsync(factory, $"een-adres-{Guid.NewGuid():N}@example.com");
        var graph = GraphJson([Node("s", "trigger.manual"), Mail("m", to)], [Edge("s", "m")]);
        var before = factory.Mail.Sent.Count;
        var run = await RunAsync(tenant, await CreateWorkflowAsync(tenant, "Mail", graph));
        Assert.Equal(before, factory.Mail.Sent.Count);
        Assert.Equal("fout", run.Status);
        Assert.Contains("één e-mailadres", run.LastMessage);
        Assert.DoesNotContain("\n", run.LastMessage);
    }

    [Fact]
    public async Task Subject_cannot_add_extra_mail_headers()
    {
        var tenant = await RegisterAsync(factory, "onderwerp@example.com");
        var graph = GraphJson([Node("s", "trigger.manual"), Mail("m", "klant@example.org", "Factuur\r\nBcc: iedereen@example.org")], [Edge("s", "m")]);
        var run = await RunAsync(tenant, await CreateWorkflowAsync(tenant, "Mail", graph));
        Assert.Equal("klaar", run.Status);
        var mail = factory.Mail.Sent.Last();
        Assert.Equal("klant@example.org", mail.To);
        Assert.Equal("Factuur Bcc: iedereen@example.org", mail.Subject);
    }

    [Fact]
    public async Task Trial_workspace_has_a_low_daily_limit_and_the_run_ends_cleanly()
    {
        var tenant = await RegisterAsync(factory, "daglimiet@example.com");
        var id = await CreateWorkflowAsync(tenant, "Vijf mails", Chain(5, "klant@example.org"));
        var before = factory.Mail.Sent.Count;

        var run = await RunAsync(tenant, id);
        Assert.Equal(3, factory.Mail.Sent.Count - before);
        Assert.Equal("klaar", run.Status);
        Assert.Equal(2, run.Log.Count(e => e.GetProperty("status").GetString() == "let op" && e.GetProperty("message").GetString()!.Contains("daglimiet")));
        Assert.Contains(run.Messages, m => m.StartsWith("Taak \"Na de mails\""));

        // Een volgende run verstuurt vandaag niets meer, maar loopt wel netjes af.
        var again = await RunAsync(tenant, id);
        Assert.Equal(3, factory.Mail.Sent.Count - before);
        Assert.Equal("klaar", again.Status);
    }

    [Fact]
    public async Task Daily_limit_starts_fresh_the_next_day()
    {
        var tenant = await RegisterAsync(factory, "nieuwe-dag@example.com");
        var ws = await WorkspaceIdAsync(tenant);
        // Gisteren zat de werkruimte al aan de limiet.
        InWorkspace(factory, ws, db =>
        {
            var w = db.Workspaces.Find(ws)!;
            w.EmailDay = DateTime.UtcNow.Date.AddDays(-1);
            w.EmailsSent = 3;
            return db.SaveChanges();
        });
        await RunAsync(tenant, await CreateWorkflowAsync(tenant, "Twee mails", Chain(2, "vandaag@example.org")));
        Assert.Equal(2, factory.Mail.Sent.Count(m => m.To == "vandaag@example.org"));
        var (day, sent) = InWorkspace(factory, ws, db => db.Workspaces.Where(w => w.Id == ws).Select(w => new { w.EmailDay, w.EmailsSent }).AsEnumerable().Select(w => (w.EmailDay, w.EmailsSent)).Single());
        Assert.Equal(DateTime.UtcNow.Date, day);
        Assert.Equal(2, sent);
    }

    [Fact]
    public async Task Paid_plan_has_a_higher_daily_limit_and_each_workspace_has_its_own()
    {
        var paid = await RegisterAsync(factory, "betaald-mail@example.com");
        await SetPlanAsync(factory, await WorkspaceIdAsync(paid), "zzp");
        var trial = await RegisterAsync(factory, "proef-mail@example.com");

        var paidFlow = await CreateWorkflowAsync(paid, "Vier mails", Chain(4, "betaald@example.org"));
        await RunAsync(paid, paidFlow);
        await RunAsync(paid, paidFlow);
        var trialFlow = await CreateWorkflowAsync(trial, "Vier mails", Chain(4, "proef@example.org"));
        await RunAsync(trial, trialFlow);

        Assert.Equal(6, factory.Mail.Sent.Count(m => m.To == "betaald@example.org"));
        Assert.Equal(3, factory.Mail.Sent.Count(m => m.To == "proef@example.org"));
    }

    [Fact]
    public async Task Email_loop_cannot_mass_mail_from_a_trial_workspace()
    {
        var tenant = await RegisterAsync(factory, "spammer@example.com");
        var graph = GraphJson([Node("s", "trigger.manual"), Mail("m", "slachtoffer@example.org", "Uw bankrekening is geblokkeerd")], [Edge("s", "m"), Edge("m", "m")]);
        var id = await CreateWorkflowAsync(tenant, "Spam", graph);
        for (var i = 0; i < 3; i++) await RunAsync(tenant, id);
        Assert.Equal(3, factory.Mail.Sent.Count(m => m.To == "slachtoffer@example.org"));
    }
}

// De echte SmtpEmailSender tegen een nepmailserver, met dezelfde lege afzender als in appsettings.json.
public class SmtpMailFactory : PortalFactory
{
    public FakeSmtpServer Smtp { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Smtp:Host", "127.0.0.1");
        builder.UseSetting("Smtp:Port", Smtp.Port.ToString());
        builder.UseSetting("Smtp:EnableSsl", "false");
        builder.UseSetting("Smtp:User", "mailer@potel.test");
        builder.UseSetting("Smtp:Password", "geheim");
        builder.UseSetting("Smtp:From", "");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Smtp.Dispose();
    }
}

public class SmtpMailTests(SmtpMailFactory factory) : IClassFixture<SmtpMailFactory>
{
    [Fact]
    public async Task Mail_comes_from_the_platform_with_the_company_name_and_replies_go_to_the_workspace()
    {
        var tenant = await RegisterAsync(factory, "jansen@example.com");
        var graph = GraphJson(
            [Node("s", "trigger.manual"), Node("m", "action.email", new() { ["to"] = "klant@example.org", ["subject"] = "Je offerte", ["body"] = "Hoi!" })],
            [Edge("s", "m")]);
        var run = await RunAsync(tenant, await CreateWorkflowAsync(tenant, "Mail", graph));
        Assert.True(run.Status == "klaar", string.Join(" | ", run.Messages));

        Assert.True(await WaitUntil(() => factory.Smtp.Messages.Any(m => m.Contains("RCPT TO:<klant@example.org>")), TimeSpan.FromSeconds(5)));
        // Lange kopregels mogen over meerdere regels staan; plak ze weer aan elkaar.
        // De bevestigingsmail van het aanmelden ging ook via deze server; het gaat hier om de mail van de werkstroom.
        var raw = Regex.Replace(factory.Smtp.Messages.Single(m => m.Contains("RCPT TO:<klant@example.org>")), @"\r\n[ \t]+", " ");
        var lines = raw.Split("\r\n");
        Assert.Contains("MAIL FROM:<mailer@potel.test>", lines);
        Assert.Equal(["RCPT TO:<klant@example.org>"], lines.Where(l => l.StartsWith("RCPT TO")));
        var from = lines.Single(l => l.StartsWith("From:"));
        Assert.Contains("Bedrijf jansen@example.com", from);
        Assert.Contains("<mailer@potel.test>", from);
        Assert.Contains("jansen@example.com", lines.Single(l => l.StartsWith("Reply-To:")));
        Assert.Equal("Subject: Je offerte", lines.Single(l => l.StartsWith("Subject:")));
    }
}

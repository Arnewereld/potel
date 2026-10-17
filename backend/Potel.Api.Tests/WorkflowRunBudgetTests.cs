using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using static Potel.Api.Tests.TestApi;

namespace Potel.Api.Tests;

// Elke run telt mee voor het budget per minuut van een werkruimte, ook runs die een trigger (nieuwe lead) start.
public class WorkflowRunBudgetFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Workflows:AllowPrivateWebhooks", "true");
        builder.UseSetting("RateLimit:WorkflowRunsPerMinute", "2");
    }
}

public class WorkflowRunBudgetTests(WorkflowRunBudgetFactory factory) : IClassFixture<WorkflowRunBudgetFactory>
{
    [Fact]
    public async Task New_leads_cannot_start_more_runs_than_the_per_minute_budget()
    {
        await using var target = await LocalServer.StartAsync();
        var tenant = await RegisterAsync(factory, "reflector@example.com");
        var workspaceId = await WorkspaceIdAsync(tenant);
        var nodes = new List<object> { WorkflowKit.Node("s", "trigger.lead") };
        var edges = new List<object>();
        for (var i = 1; i <= 10; i++)
        {
            nodes.Add(WorkflowKit.Node($"w{i}", "action.webhook", new() { ["url"] = target.Url + "/doelwit" }));
            edges.Add(WorkflowKit.Edge(i == 1 ? "s" : $"w{i - 1}", $"w{i}"));
        }
        await WorkflowKit.CreateWorkflowAsync(tenant, "Reflector", WorkflowKit.GraphJson([.. nodes], [.. edges]), active: true);

        // Tien leads: het aanmaken lukt gewoon, maar er starten maar twee runs (van tien webhooks elk).
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Created, (await tenant.PostAsJsonAsync("/api/leads", new { name = $"Lead {i}" })).StatusCode);
        Assert.True(target.Hits <= 20, $"10 leads gaven {target.Hits} webhooks");
        Assert.Equal(2, WorkflowKit.InWorkspace(factory, workspaceId, db => db.WorkflowRuns.Count()));
        Assert.True(WorkflowKit.InWorkspace(factory, workspaceId, db => db.Activities.Any(a => a.Text.Contains("niet gestart"))));

        // Het budget geldt ook voor handmatig starten.
        var manual = await WorkflowKit.CreateWorkflowAsync(tenant, "Handmatig", WorkflowKit.GraphJson([WorkflowKit.Node("s", "trigger.manual")], []));
        var refused = await tenant.PostAsJsonAsync($"/api/workflows/{manual}/run", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Contains("minuut", await refused.ErrorAsync());

        // Een andere werkruimte heeft een eigen budget.
        var other = await RegisterAsync(factory, "buurman@example.com");
        var own = await WorkflowKit.CreateWorkflowAsync(other, "Handmatig", WorkflowKit.GraphJson([WorkflowKit.Node("s", "trigger.manual")], []));
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync($"/api/workflows/{own}/run", new { })).StatusCode);
    }
}

// Webhooks hebben een daglimiet per werkruimte, net als e-mail.
public class WebhookQuotaFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Workflows:AllowPrivateWebhooks", "true");
        builder.UseSetting("Workflows:WebhooksPerDayTrial", "3");
    }
}

public class WebhookQuotaTests(WebhookQuotaFactory factory) : IClassFixture<WebhookQuotaFactory>
{
    [Fact]
    public async Task A_trial_workspace_sends_at_most_its_daily_webhooks()
    {
        await using var target = await LocalServer.StartAsync();
        var tenant = await RegisterAsync(factory, "haken@example.com");
        var nodes = new List<object> { WorkflowKit.Node("s", "trigger.manual") };
        var edges = new List<object>();
        for (var i = 1; i <= 5; i++)
        {
            nodes.Add(WorkflowKit.Node($"w{i}", "action.webhook", new() { ["url"] = target.Url + "/doelwit" }));
            edges.Add(WorkflowKit.Edge(i == 1 ? "s" : $"w{i - 1}", $"w{i}"));
        }
        var run = await WorkflowKit.RunAsync(tenant, await WorkflowKit.CreateWorkflowAsync(tenant, "Haken", WorkflowKit.GraphJson([.. nodes], [.. edges])));
        Assert.Equal(3, target.Hits);
        Assert.Contains(run.Messages, m => m.Contains("daglimiet van 3 webhooks"));

        // Een andere werkruimte heeft zijn eigen daglimiet.
        var other = await RegisterAsync(factory, "buren@example.com");
        await WorkflowKit.RunWebhookAsync(other, target.Url + "/doelwit");
        Assert.Equal(4, target.Hits);
    }
}

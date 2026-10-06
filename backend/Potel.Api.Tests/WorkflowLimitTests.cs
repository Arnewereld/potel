using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Potel.Api.Data;
using Potel.Api.Workflows;
using static Potel.Api.Tests.TestApi;
using static Potel.Api.Tests.WorkflowKit;

namespace Potel.Api.Tests;

// Kleine grenzen, zodat de tests ze snel raken. Webhooks naar 127.0.0.1 mogen hier, voor de trage testserver.
public class SchedulerLimitsFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Workflows:MaxRunsPerTick", "3");
        builder.UseSetting("Workflows:MaxWaitsPerRun", "3");
        builder.UseSetting("Workflows:MaxStepsPerRun", "12");
        builder.UseSetting("Workflows:AllowPrivateWebhooks", "true");
    }
}

// De planner op de achtergrond: niets doen voor verlopen proefperiodes, en één werkruimte mag de rest niet ophouden.
public class WorkflowSchedulerTests(SchedulerLimitsFactory factory) : IClassFixture<SchedulerLimitsFactory>
{
    static string HourlyTask(string title) => GraphJson(
        [Node("s", "trigger.schedule", new() { ["interval"] = "uur" }), Node("t", "action.task", new() { ["title"] = title, ["days"] = "0" })],
        [Edge("s", "t")]);

    static string WaitThenTask(string title) => GraphJson(
        [Node("s", "trigger.manual"), Node("w", "logic.wait", new() { ["amount"] = "1", ["unit"] = "minuten" }), Node("t", "action.task", new() { ["title"] = title, ["days"] = "0" })],
        [Edge("s", "w"), Edge("w", "t")]);

    // Doet alsof de wachttijd van een run voorbij is.
    void MakeDue(int workspaceId, int runId) => InWorkspace(factory, workspaceId, db =>
    {
        var r = db.WorkflowRuns.Find(runId)!;
        var past = DateTime.UtcNow.AddMinutes(-1);
        var pending = JsonSerializer.Deserialize<List<PendingStep>>(r.PendingJson, Graph.Json)!;
        r.PendingJson = JsonSerializer.Serialize(pending.Select(p => p with { ResumeAt = past }).ToList(), Graph.Json);
        r.ResumeAt = past;
        return db.SaveChanges();
    });

    WorkflowRun Load(int workspaceId, int runId) => InWorkspace(factory, workspaceId, db => db.WorkflowRuns.Find(runId)!);

    static string LastMessage(WorkflowRun r) => JsonSerializer.Deserialize<List<LogEntry>>(r.LogJson, Graph.Json)!.Last().Message;

    async Task ResumeDueAsync(int workspaceId)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = workspaceId;
        await scope.ServiceProvider.GetRequiredService<WorkflowEngine>().ResumeDueAsync();
    }

    async Task<WorkflowRun> ResumeRepeatedlyAsync(int workspaceId, int runId, int times)
    {
        for (var i = 0; i < times && Load(workspaceId, runId).Status == "wachtend"; i++)
        {
            MakeDue(workspaceId, runId);
            await ResumeDueAsync(workspaceId);
        }
        return Load(workspaceId, runId);
    }

    [Fact]
    public void Database_runs_in_wal_mode_so_parallel_workspaces_do_not_lock_each_other()
    {
        var mode = InWorkspace(factory, 0, db => db.Database.SqlQueryRaw<string>("SELECT journal_mode AS Value FROM pragma_journal_mode").AsEnumerable().Single());
        Assert.Equal("wal", mode);
    }

    [Fact]
    public async Task Scheduler_skips_workspaces_whose_trial_expired()
    {
        var expired = await RegisterAsync(factory, "planner-verlopen@example.com");
        var expiredWs = await WorkspaceIdAsync(expired);
        await CreateWorkflowAsync(expired, "Elk uur", HourlyTask("Na de proef"), active: true);
        // Ook een wachtende run die al verder had gemogen, blijft liggen.
        var waiting = await RunAsync(expired, await CreateWorkflowAsync(expired, "Wachten", WaitThenTask("Na het wachten")));
        MakeDue(expiredWs, waiting.Id);
        await ExpireTrialAsync(factory, expiredWs);
        Assert.Equal(HttpStatusCode.PaymentRequired, (await expired.PostAsJsonAsync("/api/customers", new { name = "X" })).StatusCode);

        var active = await RegisterAsync(factory, "planner-actief@example.com");
        var activeWs = await WorkspaceIdAsync(active);
        await CreateWorkflowAsync(active, "Elk uur", HourlyTask("Taak van actieve klant"), active: true);

        var (reached, _) = await RunSchedulerUntil(factory, () => HasAppointment(factory, activeWs, "Taak van actieve klant"), TimeSpan.FromSeconds(15));
        Assert.True(reached, "De planner draaide de werkstroom van een actieve werkruimte niet");
        Assert.False(HasAppointment(factory, expiredWs, "Na de proef"), "De planner schreef in een werkruimte met een verlopen proef");
        Assert.False(HasAppointment(factory, expiredWs, "Na het wachten"));
        Assert.Equal("wachtend", Load(expiredWs, waiting.Id).Status);
    }

    [Fact]
    public async Task Triggers_and_manual_runs_do_not_start_in_an_expired_workspace()
    {
        var tenant = await RegisterAsync(factory, "trigger-verlopen@example.com");
        var ws = await WorkspaceIdAsync(tenant);
        var graph = GraphJson([Node("s", "trigger.lead"), Node("t", "action.task", new() { ["title"] = "Lead opvolgen", ["days"] = "0" })], [Edge("s", "t")]);
        var id = await CreateWorkflowAsync(tenant, "Bij een lead", graph, active: true);
        await ExpireTrialAsync(factory, ws);

        Assert.Equal(HttpStatusCode.PaymentRequired, (await tenant.PostAsJsonAsync($"/api/workflows/{id}/run", new { })).StatusCode);

        // Ook als de motor rechtstreeks wordt aangeroepen, start er niets.
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = ws;
            var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
            await engine.TriggerAsync("trigger.lead", new() { ["lead.name"] = "X" }, "Nieuwe lead: X");
        }
        Assert.Equal(0, InWorkspace(factory, ws, db => db.WorkflowRuns.Count()));
        Assert.False(HasAppointment(factory, ws, "Lead opvolgen"));
    }

    [Fact]
    public async Task Slow_workflow_in_one_workspace_does_not_delay_other_workspaces()
    {
        await using var slow = await LocalServer.StartAsync(async ctx =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            await ctx.Response.WriteAsync("ok");
        });
        var attacker = await RegisterAsync(factory, "traag@example.com");
        var hook = new Dictionary<string, string> { ["url"] = slow.Url + "/traag" };
        await CreateWorkflowAsync(attacker, "Traag", GraphJson(
            [Node("s", "trigger.schedule", new() { ["interval"] = "uur" }), Node("w1", "action.webhook", hook), Node("w2", "action.webhook", hook)],
            [Edge("s", "w1"), Edge("w1", "w2")]), active: true);

        var victim = await RegisterAsync(factory, "niet-ophouden@example.com");
        var victimWs = await WorkspaceIdAsync(victim);
        await CreateWorkflowAsync(victim, "Elk uur", HourlyTask("Taak van slachtoffer"), active: true);

        var (reached, elapsed) = await RunSchedulerUntil(factory, () => HasAppointment(factory, victimWs, "Taak van slachtoffer"), TimeSpan.FromSeconds(30));
        Assert.True(reached, "De geplande werkstroom van de andere werkruimte liep niet");
        Assert.True(elapsed < TimeSpan.FromSeconds(4), $"De andere werkruimte wachtte {elapsed.TotalSeconds:F1}s op trage webhooks van een ander (hits: {slow.Hits})");
    }

    [Fact]
    public async Task One_workspace_handles_a_limited_number_of_runs_per_round()
    {
        var tenant = await RegisterAsync(factory, "veel-runs@example.com");
        var ws = await WorkspaceIdAsync(tenant);
        var id = await CreateWorkflowAsync(tenant, "Wachten", WaitThenTask("Na het wachten"));
        for (var i = 0; i < 5; i++) MakeDue(ws, (await RunAsync(tenant, id)).Id);

        int Count(string status) => InWorkspace(factory, ws, db => db.WorkflowRuns.Count(r => r.Status == status));
        var (reached, _) = await RunSchedulerUntil(factory, () => Count("klaar") >= 3, TimeSpan.FromSeconds(15));
        Assert.True(reached);
        Assert.Equal(3, Count("klaar"));
        Assert.Equal(2, Count("wachtend"));
    }

    [Fact]
    public async Task Wait_loop_stops_after_the_maximum_number_of_waits()
    {
        var tenant = await RegisterAsync(factory, "wachtlus@example.com");
        var ws = await WorkspaceIdAsync(tenant);
        var graph = GraphJson([Node("s", "trigger.manual"), Node("w", "logic.wait", new() { ["amount"] = "0", ["unit"] = "minuten" })], [Edge("s", "w"), Edge("w", "w")]);
        var run = await RunAsync(tenant, await CreateWorkflowAsync(tenant, "Wachtlus", graph));
        Assert.Equal("wachtend", run.Status);
        Assert.True(Load(ws, run.Id).ResumeAt >= DateTime.UtcNow.AddSeconds(30), "Wachten duurt minstens een minuut");

        var done = await ResumeRepeatedlyAsync(ws, run.Id, times: 10);
        Assert.Equal("fout", done.Status);
        Assert.Contains("3 keer wachten", LastMessage(done));
    }

    [Fact]
    public async Task Step_limit_counts_every_step_of_a_run_also_after_waiting()
    {
        var tenant = await RegisterAsync(factory, "stappen@example.com");
        var ws = await WorkspaceIdAsync(tenant);
        var task = (string title) => new Dictionary<string, string> { ["title"] = title, ["days"] = "0" };
        var graph = GraphJson(
            [Node("s", "trigger.manual"), Node("w", "logic.wait", new() { ["amount"] = "1", ["unit"] = "minuten" }),
             Node("a", "action.task", task("A")), Node("b", "action.task", task("B")), Node("c", "action.task", task("C"))],
            [Edge("s", "w"), Edge("w", "a"), Edge("a", "b"), Edge("b", "c"), Edge("c", "w")]);
        var run = await RunAsync(tenant, await CreateWorkflowAsync(tenant, "Rondje", graph));

        var done = await ResumeRepeatedlyAsync(ws, run.Id, times: 10);
        Assert.Equal("fout", done.Status);
        Assert.Contains("Gestopt na 12 stappen", LastMessage(done));
    }

    [Fact]
    public async Task A_broken_run_does_not_stop_other_runs_or_schedules_in_the_same_round()
    {
        // Werkruimte 1: een wachtende run met onleesbare gegevens vóór een goede run en een schema.
        var one = await RegisterAsync(factory, "kapotte-run@example.com");
        var oneWs = await WorkspaceIdAsync(one);
        var waitFlow = await CreateWorkflowAsync(one, "Wachten", WaitThenTask("Goede run klaar"));
        var broken = await RunAsync(one, waitFlow);
        var good = await RunAsync(one, waitFlow);
        MakeDue(oneWs, broken.Id);
        MakeDue(oneWs, good.Id);
        InWorkspace(factory, oneWs, db => { db.WorkflowRuns.Find(broken.Id)!.ContextJson = "{kapot"; return db.SaveChanges(); });
        await CreateWorkflowAsync(one, "Elk uur", HourlyTask("Schema na kapotte run"), active: true);

        // Werkruimte 2: een kapot schema (twee blokken met hetzelfde id) vóór een goed schema.
        var two = await RegisterAsync(factory, "kapot-schema@example.com");
        var twoWs = await WorkspaceIdAsync(two);
        var duplicate = GraphJson(
            [Node("s", "trigger.schedule", new() { ["interval"] = "uur" }), Node("t", "action.task", new() { ["title"] = "Dubbel", ["days"] = "0" }), Node("t", "action.task")],
            [Edge("s", "t")]);
        await CreateWorkflowAsync(two, "Kapot", duplicate, active: true);
        await CreateWorkflowAsync(two, "Elk uur", HourlyTask("Schema na kapot schema"), active: true);

        var (reached, _) = await RunSchedulerUntil(factory,
            () => HasAppointment(factory, oneWs, "Schema na kapotte run") && HasAppointment(factory, twoWs, "Schema na kapot schema") && Load(oneWs, good.Id).Status == "klaar",
            TimeSpan.FromSeconds(15));
        Assert.True(reached, "Een kapotte run of werkstroom hield de rest van de werkruimte tegen");
        Assert.True(HasAppointment(factory, oneWs, "Goede run klaar"));
        // De kapotte run komt niet elke ronde terug.
        Assert.Equal("fout", Load(oneWs, broken.Id).Status);
    }
}

public class StrictRunLimitFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("RateLimit:WorkflowRunsPerMinute", "2");
    }
}

// Handmatig uitvoeren is per werkruimte begrensd, zodat één werkruimte de server niet bezig kan houden.
public class WorkflowRunRateTests(StrictRunLimitFactory factory) : IClassFixture<StrictRunLimitFactory>
{
    [Fact]
    public async Task Manual_runs_are_limited_per_workspace()
    {
        var graph = GraphJson([Node("s", "trigger.manual"), Node("t", "action.task", new() { ["title"] = "Handmatig", ["days"] = "0" })], [Edge("s", "t")]);
        var a = await RegisterAsync(factory, "veel-uitvoeren@example.com");
        var b = await RegisterAsync(factory, "ander-uitvoeren@example.com");
        var flowA = await CreateWorkflowAsync(a, "Taak", graph);
        var flowB = await CreateWorkflowAsync(b, "Taak", graph);

        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync($"/api/workflows/{flowA}/run", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsJsonAsync($"/api/workflows/{flowA}/run", new { })).StatusCode);
        var third = await a.PostAsJsonAsync($"/api/workflows/{flowA}/run", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Contains("rustig aan", await third.ErrorAsync());
        Assert.Equal(HttpStatusCode.OK, (await b.PostAsJsonAsync($"/api/workflows/{flowB}/run", new { })).StatusCode);
    }
}

public class ShortRunFactory : PortalFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("Workflows:MaxSecondsPerRun", "2");
        builder.UseSetting("Workflows:AllowPrivateWebhooks", "true");
    }
}

// Een run heeft een maximale looptijd, ook als een webhook lang op zich laat wachten.
public class WorkflowRunTimeTests(ShortRunFactory factory) : IClassFixture<ShortRunFactory>
{
    [Fact]
    public async Task Run_stops_when_it_takes_too_long()
    {
        await using var slow = await LocalServer.StartAsync(async ctx =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4), ctx.RequestAborted);
            await ctx.Response.WriteAsync("ok");
        });
        var tenant = await RegisterAsync(factory, "te-lang@example.com");
        var graph = GraphJson(
            [Node("s", "trigger.manual"), Node("w", "action.webhook", new() { ["url"] = slow.Url + "/traag" }), Node("t", "action.task", new() { ["title"] = "Na de webhook", ["days"] = "0" })],
            [Edge("s", "w"), Edge("w", "t")]);
        var id = await CreateWorkflowAsync(tenant, "Traag", graph);

        var sw = Stopwatch.StartNew();
        var run = await RunAsync(tenant, id);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"De run duurde {sw.Elapsed.TotalSeconds:F1}s");
        Assert.Equal("fout", run.Status);
        Assert.Contains("langer dan 2 seconden", run.LastMessage);
        Assert.DoesNotContain(run.Messages, m => m.StartsWith("Taak"));
    }
}

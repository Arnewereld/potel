using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Potel.Api.Data;

namespace Potel.Api.Workflows;

// Draait op de achtergrond: zet wachtende runs voort en start geplande werkstromen, per werkruimte.
// Werkruimtes lopen naast elkaar (hooguit ParallelWorkspaces tegelijk), zodat een trage werkstroom bij de een
// de ander niet ophoudt. Elke run krijgt een eigen scope met de juiste werkruimte, en een fout in één run of
// werkruimte stopt de rest niet. Werkruimtes met een verlopen proef slaat de planner over.
public class WorkflowScheduler(IServiceScopeFactory scopes, IOptions<WorkflowLimits> options, ILogger<WorkflowScheduler> logger) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    readonly WorkflowLimits limits = options.Value;
    // Werkruimtes die nog bezig zijn met een vorige ronde; die slaat een nieuwe ronde over.
    readonly ConcurrentDictionary<int, Task> busy = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var slots = new SemaphoreSlim(Math.Max(1, limits.ParallelWorkspaces));
        using var timer = new PeriodicTimer(Interval);
        try
        {
            do
            {
                try
                {
                    await TickAsync(slots, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Werkstroomplanner mislukt");
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        finally
        {
            // Bij het stoppen maken lopende werkruimtes hun huidige run af.
            await Task.WhenAll(busy.Values);
        }
    }

    async Task TickAsync(SemaphoreSlim slots, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        List<int> workspaces;
        using (var scope = scopes.CreateScope())
            workspaces = (await scope.ServiceProvider.GetRequiredService<AppDb>().Workspaces.AsNoTracking().OrderBy(w => w.Id).ToListAsync(ct))
                .Where(w => !w.TrialExpired(now)).Select(w => w.Id).ToList();

        foreach (var (id, task) in busy)
            if (task.IsCompleted) busy.TryRemove(id, out _);

        foreach (var id in workspaces)
        {
            if (busy.ContainsKey(id)) continue;
            await slots.WaitAsync(ct);
            busy[id] = Task.Run(async () =>
            {
                try { await RunWorkspaceAsync(id, ct); }
                finally { slots.Release(); }
            }, CancellationToken.None);
        }
    }

    // Eén werkruimte: eerst wachtende runs, dan geplande werkstromen, samen hooguit MaxRunsPerTick runs.
    // Is de werkruimte deze ronde al MaxSecondsPerRun bezig, dan begint hij geen nieuwe run meer; de rest volgt de volgende ronde.
    async Task RunWorkspaceAsync(int workspaceId, CancellationToken ct)
    {
        try
        {
            var now = DateTime.Now;
            List<int> runs, schedules;
            using (var scope = Scope(workspaceId, out var engine))
            {
                runs = await engine.DueRunIdsAsync(limits.MaxRunsPerTick);
                schedules = await engine.DueScheduleIdsAsync(now, limits.MaxRunsPerTick - runs.Count);
            }

            var budget = Stopwatch.StartNew();
            bool OutOfTime() => ct.IsCancellationRequested || budget.Elapsed > TimeSpan.FromSeconds(limits.MaxSecondsPerRun);
            foreach (var id in runs)
            {
                if (OutOfTime()) return;
                // Een run die niet verder kan, gaat op fout, zodat hij niet elke ronde opnieuw vastloopt.
                if (!await RunInOwnScopeAsync(workspaceId, e => e.ResumeAsync(id)))
                    await RunInOwnScopeAsync(workspaceId, e => e.FailAsync(id));
            }
            foreach (var id in schedules)
            {
                if (OutOfTime()) return;
                await RunInOwnScopeAsync(workspaceId, e => e.RunScheduleAsync(id, now));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Werkstroomplanner mislukt voor werkruimte {Workspace}", workspaceId);
        }
    }

    IServiceScope Scope(int workspaceId, out WorkflowEngine engine)
    {
        var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = workspaceId;
        engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
        return scope;
    }

    // Elke run in een eigen scope, zodat een fout in de ene run de databasecontext van de volgende niet raakt.
    async Task<bool> RunInOwnScopeAsync(int workspaceId, Func<WorkflowEngine, Task> work)
    {
        try
        {
            using var scope = Scope(workspaceId, out var engine);
            await work(engine);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Werkstroomrun mislukt in werkruimte {Workspace}", workspaceId);
            return false;
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Potel.Api.Data;

namespace Potel.Api.Workflows;

// Draait op de achtergrond: zet wachtende runs voort en start geplande werkstromen, per werkruimte.
public class WorkflowScheduler(IServiceScopeFactory scopes, ILogger<WorkflowScheduler> logger) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            List<int> workspaces;
            using (var scope = scopes.CreateScope())
                workspaces = await scope.ServiceProvider.GetRequiredService<AppDb>().Workspaces.Select(w => w.Id).ToListAsync(stoppingToken);

            foreach (var id in workspaces)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    scope.ServiceProvider.GetRequiredService<Tenant>().WorkspaceId = id;
                    var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
                    await engine.ResumeDueAsync();
                    await engine.RunSchedulesAsync(DateTime.Now);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Werkstroomplanner mislukt voor werkruimte {Workspace}", id);
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

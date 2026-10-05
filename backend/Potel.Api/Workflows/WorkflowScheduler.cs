namespace Potel.Api.Workflows;

// Draait op de achtergrond: zet wachtende runs voort en start geplande werkstromen.
public class WorkflowScheduler(IServiceScopeFactory scopes, ILogger<WorkflowScheduler> logger) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngine>();
                await engine.ResumeDueAsync();
                await engine.RunSchedulesAsync(DateTime.Now);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Werkstroomplanner mislukt");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

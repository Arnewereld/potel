using System.Threading.RateLimiting;
using Potel.Api.Data;

namespace Potel.Api.Workflows;

// Grenzen voor werkstromen, zodat één werkruimte de server niet kan volzetten of andere werkruimtes laat wachten.
// Aan te passen in appsettings.json onder "Workflows".
public class WorkflowLimits
{
    // Eén run: hooguit zoveel blokken (ook over het wachten heen), zoveel seconden per keer dat hij loopt,
    // zoveel e-mails en webhooks samen, en zoveel keer wachten. Daarna stopt hij met een melding.
    public int MaxStepsPerRun { get; set; } = 200;
    public int MaxSecondsPerRun { get; set; } = 30;
    public int MaxCallsPerRun { get; set; } = 10;
    public int MaxWaitsPerRun { get; set; } = 20;

    // De planner: per ronde (elke 30 seconden) hooguit zoveel runs per werkruimte, en zoveel werkruimtes tegelijk.
    public int MaxRunsPerTick { get; set; } = 10;
    public int ParallelWorkspaces { get; set; } = 4;

    // E-mails per werkruimte per dag: laag tijdens de proef, hoger met een abonnement.
    public int EmailsPerDayTrial { get; set; } = 20;
    public int EmailsPerDayPaid { get; set; } = 200;
    // Alle proefwerkruimtes samen per dag. Een proef is gratis en snel aangemaakt; zo blijft de mailserver van het platform heel.
    public int EmailsPerDayAllTrials { get; set; } = 500;

    // Webhooks per werkruimte per dag, net als e-mail.
    public int WebhooksPerDayTrial { get; set; } = 100;
    public int WebhooksPerDayPaid { get; set; } = 1000;

    public int WebhookTimeoutSeconds { get; set; } = 5;
    // Alleen voor tests of je eigen server: webhooks ook naar interne adressen en andere poorten toestaan.
    public bool AllowPrivateWebhooks { get; set; }

    public int EmailsPerDay(string plan) => plan == Plans.Trial ? EmailsPerDayTrial : EmailsPerDayPaid;
    public int WebhooksPerDay(string plan) => plan == Plans.Trial ? WebhooksPerDayTrial : WebhooksPerDayPaid;
}

// Hoeveel runs een werkruimte per minuut mag starten, of dat nu handmatig, door een trigger (nieuwe lead, klant, betaling)
// of door de planner gebeurt. Zo kan niemand met een stroom nieuwe leads de server eindeloos webhooks laten versturen.
public sealed class WorkflowRunBudget(IConfiguration config) : IDisposable
{
    public int PerMinute { get; } = Math.Max(1, config.GetValue("RateLimit:WorkflowRunsPerMinute", 30));

    readonly PartitionedRateLimiter<int> limiter = PartitionedRateLimiter.Create<int, int>(workspace =>
        RateLimitPartition.GetFixedWindowLimiter(workspace, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, config.GetValue("RateLimit:WorkflowRunsPerMinute", 30)), Window = TimeSpan.FromMinutes(1),
        }));

    public bool TryStart(int workspaceId)
    {
        using var lease = limiter.AttemptAcquire(workspaceId);
        return lease.IsAcquired;
    }

    public void Dispose() => limiter.Dispose();
}

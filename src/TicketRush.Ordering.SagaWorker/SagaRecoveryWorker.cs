using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TicketRush.Ordering.Persistence;

namespace TicketRush.Ordering.SagaWorker;

/// <summary>Periodically checks overdue intents; persistent retry timing lives in Ordering DB.</summary>
public sealed class SagaRecoveryWorker(
    IServiceScopeFactory scopes, ILogger<SagaRecoveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Bounded per-poll work prevents an old backlog from starving
                // RabbitMQ consumer tasks in this host.
                for (var i = 0; i < 25; i++)
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var dispatcher = scope.ServiceProvider.GetRequiredService<SagaRecoveryDispatcher>();
                    var outcome = await dispatcher.RecoverNextAsync(stoppingToken);
                    if (outcome == SagaRecoveryOutcome.None) break;
                    if (outcome == SagaRecoveryOutcome.NeedsAttention)
                        logger.LogWarning(
                            "Ordering saga {OrderId} requires operator attention after recovery checks",
                            dispatcher.LastProcessedOrderId);
                    else
                        logger.LogInformation("Ordering saga {OrderId} recovery: {Outcome}",
                            dispatcher.LastProcessedOrderId, outcome);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ordering saga recovery scan failed; retrying");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}

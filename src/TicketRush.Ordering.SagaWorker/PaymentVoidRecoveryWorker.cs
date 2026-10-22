using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TicketRush.Ordering.Persistence;

namespace TicketRush.Ordering.SagaWorker;

/// <summary>Periodically checks overdue intents; persistent retry timing lives in Ordering DB.</summary>
public sealed class PaymentVoidRecoveryWorker(
    IServiceScopeFactory scopes, ILogger<PaymentVoidRecoveryWorker> logger) : BackgroundService
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
                    var dispatcher = scope.ServiceProvider.GetRequiredService<PaymentVoidRecoveryDispatcher>();
                    var outcome = await dispatcher.RecoverNextAsync(stoppingToken);
                    if (outcome == VoidRecoveryOutcome.None) break;
                    if (outcome == VoidRecoveryOutcome.NeedsAttention)
                        logger.LogWarning(
                            "Payment void {OrderId} requires operator attention after recovery checks",
                            dispatcher.LastProcessedOrderId);
                    else
                        logger.LogInformation("Payment void {OrderId} recovery: {Outcome}",
                            dispatcher.LastProcessedOrderId, outcome);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Payment void recovery scan failed; retrying");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}

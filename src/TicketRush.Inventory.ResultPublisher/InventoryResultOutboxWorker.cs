using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TicketRush.Infrastructure;

namespace TicketRush.Inventory.ResultPublisher;

public sealed class InventoryResultOutboxWorker(
    IServiceScopeFactory scopes, ILogger<InventoryResultOutboxWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider
                    .GetRequiredService<InventoryResultOutboxDispatcher>();
                var outcome = await dispatcher.DispatchNextAsync(stoppingToken);
                if (outcome == InventoryResultDispatchOutcome.Empty)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                else if (outcome == InventoryResultDispatchOutcome.Deferred)
                    logger.LogWarning("Inventory result outbox publication failed; retry scheduled");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inventory result outbox dispatch failed");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }
}

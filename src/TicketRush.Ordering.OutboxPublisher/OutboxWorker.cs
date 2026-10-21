using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using TicketRush.Ordering.Persistence;

namespace TicketRush.Ordering.OutboxPublisher;

public sealed class OutboxWorker(IServiceScopeFactory scopes, ILogger<OutboxWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
                var outcome = await dispatcher.DispatchNextAsync(stoppingToken);
                if (outcome == DispatchOutcome.Empty)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                else if (outcome == DispatchOutcome.Deferred)
                    logger.LogWarning("An outbox publication failed; retry has been scheduled");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox dispatch failed before delivery state was committed");
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
    }
}

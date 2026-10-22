using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TicketRush.Payment.Persistence;

namespace TicketRush.Payment.Worker;

public sealed class PaymentOutboxWorker(
    IServiceScopeFactory scopes, ILogger<PaymentOutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<PaymentOutboxDispatcher>();
                var result = await dispatcher.DispatchNextAsync(stoppingToken);
                if (result == PaymentDispatchOutcome.Empty)
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                else if (result == PaymentDispatchOutcome.Deferred)
                    logger.LogWarning("Payment result publication deferred");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "Payment result outbox failure");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.Ordering.Persistence;

namespace TicketRush.Ordering.SagaWorker;

public sealed class CheckoutRequestedWorker(
    IServiceScopeFactory scopes, IConfiguration config, ILogger<CheckoutRequestedWorker> logger)
    : SagaQueueWorker(scopes, config, logger)
{
    protected override string Queue => "ticket.ordering.checkout-requested.v1";
    protected override async Task HandleAsync(OrderingSagaProcessor saga, string type,
        byte[] body, Guid messageId, CancellationToken ct)
    {
        var checkout = Deserialize<CheckoutRequestedV1>(type, body, messageId);
        await saga.ScheduleInventoryAsync(checkout, ct);
    }
}

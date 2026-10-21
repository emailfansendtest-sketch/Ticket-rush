using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.Ordering.Persistence;

namespace TicketRush.Ordering.SagaWorker;

public sealed class InventoryResultWorker(
    IServiceScopeFactory scopes, IConfiguration config, ILogger<InventoryResultWorker> logger)
    : SagaQueueWorker(scopes, config, logger)
{
    protected override string Queue => "ticket.ordering.inventory-results.v1";
    protected override async Task HandleAsync(OrderingSagaProcessor saga, string type,
        byte[] body, Guid messageId, CancellationToken ct)
    {
        switch (type)
        {
            case nameof(InventorySaleCommittedV1):
                await saga.RecordResultAsync(
                    Deserialize<InventorySaleCommittedV1>(type, body, messageId), ct);
                break;
            case nameof(InventorySaleRejectedV1):
                await saga.RecordResultAsync(
                    Deserialize<InventorySaleRejectedV1>(type, body, messageId), ct);
                break;
            default:
                throw new InvalidDataException("Unknown Inventory result type.");
        }
    }
}

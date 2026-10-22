using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TicketRush.BuildingBlocks.Contracts.Payment;
using TicketRush.Ordering.Persistence;

namespace TicketRush.Ordering.SagaWorker;

public sealed class PaymentVoidResultWorker(
    IServiceScopeFactory scopes, IConfiguration config, ILogger<PaymentVoidResultWorker> logger)
    : SagaQueueWorker(scopes, config, logger)
{
    protected override string Queue => "ticket.ordering.payment-void-results.v1";

    protected override async Task HandleAsync(OrderingSagaProcessor saga, string type,
        byte[] body, Guid messageId, CancellationToken ct)
    {
        if (type != nameof(PaymentVoidedV1))
            throw new InvalidDataException("Unexpected Payment void result type.");
        await saga.RecordVoidResultAsync(
            Deserialize<PaymentVoidedV1>(type, body, messageId), ct);
    }
}

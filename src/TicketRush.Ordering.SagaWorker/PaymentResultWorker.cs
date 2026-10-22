using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TicketRush.BuildingBlocks.Contracts.Payment;
using TicketRush.Ordering.Persistence;

namespace TicketRush.Ordering.SagaWorker;

/// <summary>Durable manual-ACK subscriber; only acknowledges after the Ordering transaction commits.</summary>
public sealed class PaymentResultWorker(
    IServiceScopeFactory scopes, IConfiguration config, ILogger<PaymentResultWorker> logger)
    : SagaQueueWorker(scopes, config, logger)
{
    protected override string Queue => "ticket.ordering.payment-results.v1";

    protected override async Task HandleAsync(OrderingSagaProcessor saga, string type,
        byte[] body, Guid messageId, CancellationToken ct)
    {
        switch (type)
        {
            case nameof(PaymentAuthorizedV1):
                await saga.RecordPaymentResultAsync(
                    Deserialize<PaymentAuthorizedV1>(type, body, messageId), ct);
                break;
            case nameof(PaymentDeclinedV1):
                await saga.RecordPaymentResultAsync(
                    Deserialize<PaymentDeclinedV1>(type, body, messageId), ct);
                break;
            default:
                throw new InvalidDataException("Unsupported Payment result event.");
        }
    }
}

using TicketRush.Domain;

namespace TicketRush.Ordering;

/// <summary>Ordering application boundary; checkout remains atomic in Phase 1.</summary>
public interface IOrderingService
{
    // A missing result also covers orders owned by a different buyer.
    Task<OrderView?> GetOrderAsync(Guid orderId, Guid buyerId, CancellationToken ct);

    Task<OperationResult<OrderView>> ConfirmAsync(
        Guid holdId, Guid buyerId, string? idempotencyKey, CancellationToken ct);
}

using TicketRush.Domain;

namespace TicketRush.Ordering;

/// <summary>Ordering application boundary; checkout remains atomic in Phase 1.</summary>
public interface IOrderingService
{
    Task<OperationResult<OrderView>> ConfirmAsync(
        Guid holdId, Guid buyerId, string? idempotencyKey, CancellationToken ct);
}

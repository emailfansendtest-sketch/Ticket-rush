using TicketRush.Domain;

namespace TicketRush.Inventory;

/// <summary>Inventory read/reservation boundary inside the Phase-1 process.</summary>
public interface IInventoryService
{
    Task<IReadOnlyList<SeatSummary>> ListSeatsAsync(Guid eventId, CancellationToken ct);

    Task<OperationResult<HoldView>> HoldAsync(
        Guid eventId, Guid buyerId, string? idempotencyKey,
        string[]? requestedCodes, CancellationToken ct);
}

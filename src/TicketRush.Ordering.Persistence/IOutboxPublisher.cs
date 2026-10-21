namespace TicketRush.Ordering.Persistence;

/// <summary>
/// Returns only after broker confirmation (including mandatory routing) is received.
/// A caller must assume delivery is ambiguous if this call throws or times out.
/// </summary>
public interface IOutboxPublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken ct);
}

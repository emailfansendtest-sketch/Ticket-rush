namespace TicketRush.Infrastructure;

/// <summary>
/// Completes only after positive tracked publisher confirmation from RabbitMQ.
/// Exceptions and timeouts must be treated as ambiguous delivery outcomes.
/// </summary>
public interface IInventoryResultPublisher
{
    Task PublishAsync(InventoryResultOutbox message, CancellationToken ct);
}

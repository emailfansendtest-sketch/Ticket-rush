namespace TicketRush.Infrastructure;

/// <summary>Durable Inventory outcome; delivery is at least once, with stable MessageId.</summary>
public sealed class InventoryResultOutbox
{
    public Guid MessageId { get; set; }
    public Guid OrderId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public int PublishAttempts { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public string? LastError { get; set; }
}

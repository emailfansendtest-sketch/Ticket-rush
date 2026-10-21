namespace TicketRush.Ordering.Persistence;

/// <summary>Exactly one terminal Inventory outcome per Ordering intent.</summary>
public sealed class OrderingResultInboxMessage
{
    public Guid MessageId { get; set; }
    public Guid OrderId { get; set; }
    public Guid HoldId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
}

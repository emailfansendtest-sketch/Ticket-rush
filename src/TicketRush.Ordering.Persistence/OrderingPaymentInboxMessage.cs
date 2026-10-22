namespace TicketRush.Ordering.Persistence;

/// <summary>One authoritative, deduplicated simulated Payment decision per order.</summary>
public sealed class OrderingPaymentInboxMessage
{
    public Guid MessageId { get; set; }
    public Guid OrderId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string? ReasonCode { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
}

namespace TicketRush.Ordering.Persistence;

/// <summary>One durable, authoritative Payment void confirmation per checkout.</summary>
public sealed class OrderingVoidInboxMessage
{
    public Guid MessageId { get; set; }
    public Guid OrderId { get; set; }
    public Guid VoidCommandId { get; set; }
    public Guid AuthorizationCommandId { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
}

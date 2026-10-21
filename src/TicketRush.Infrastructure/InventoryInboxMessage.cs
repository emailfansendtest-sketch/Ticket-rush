namespace TicketRush.Infrastructure;

/// <summary>A durably handled Inventory command, unique by both broker message and order.</summary>
public sealed class InventoryInboxMessage
{
    public Guid MessageId { get; set; }
    public Guid OrderId { get; set; }
    public Guid HoldId { get; set; }
    public Guid BuyerId { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
    public Guid ResultMessageId { get; set; }
    public string ResultType { get; set; } = string.Empty;
}

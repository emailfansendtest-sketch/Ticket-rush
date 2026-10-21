namespace TicketRush.Infrastructure;

/// <summary>Durable outcome of an Inventory command. Publication is a later step.</summary>
public sealed class InventoryResultOutbox
{
    public Guid MessageId { get; set; }
    public Guid OrderId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
}

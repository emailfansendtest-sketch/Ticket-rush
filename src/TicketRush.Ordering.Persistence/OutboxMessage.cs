namespace TicketRush.Ordering.Persistence;

/// <summary>
/// Serialized committed fact. No publisher is wired in this step; the row must
/// survive process restarts for a future at-least-once dispatcher to deliver.
/// </summary>
public sealed class OutboxMessage
{
    public Guid MessageId { get; set; }
    public Guid OrderId { get; set; }
    public string MessageType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public int PublishAttempts { get; set; }
}

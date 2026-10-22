namespace TicketRush.Payment.Persistence;

public sealed class PaymentOutboxMessage
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

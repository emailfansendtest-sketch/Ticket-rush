namespace TicketRush.Notification.Persistence;

/// <summary>
/// Durable record of simulated delivery, not proof of external email/SMS delivery.
/// </summary>
public sealed class NotificationReceipt
{
    public Guid MessageId { get; set; }
    public Guid OrderId { get; set; }
    public Guid BuyerId { get; set; }
    public string Outcome { get; set; } = null!;
    public string? ReasonCode { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

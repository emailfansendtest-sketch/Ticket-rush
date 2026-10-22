namespace TicketRush.Payment.Persistence;

/// <summary>Durable inbox for a simulated authorization void; one per OrderId.</summary>
public sealed class PaymentVoidReceipt
{
    public Guid MessageId { get; set; }
    public Guid OrderId { get; set; }
    public Guid BuyerId { get; set; }
    public Guid AuthorizationCommandId { get; set; }
    public Guid ResultMessageId { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
}

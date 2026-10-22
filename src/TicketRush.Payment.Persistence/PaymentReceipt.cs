namespace TicketRush.Payment.Persistence;

public sealed class PaymentReceipt
{
    public Guid MessageId { get; set; }
    public Guid OrderId { get; set; }
    public Guid BuyerId { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public bool SimulateDecline { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
    public Guid ResultMessageId { get; set; }
    public string ResultType { get; set; } = string.Empty;
}

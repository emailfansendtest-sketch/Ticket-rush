namespace TicketRush.Domain;

public sealed class SeatHold
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid BuyerId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public string SeatCodesCsv { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public int SeatCount { get; set; }
    public HoldStatus Status { get; set; }
}

namespace TicketRush.Domain;

public sealed class TicketOrder
{
    public Guid Id { get; set; }
    public Guid HoldId { get; set; }
    public Guid BuyerId { get; set; }
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}

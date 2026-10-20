namespace TicketRush.Domain;

// Exactly one row per event/seat: primary key makes double-sale detectable and preventable.
public sealed class SeatAllocation
{
    public Guid EventId { get; set; }
    public string SeatCode { get; set; } = string.Empty;
    public SeatStatus Status { get; set; }
    public Guid? HoldId { get; set; }
    public DateTimeOffset? HeldUntilUtc { get; set; }
    public Guid? SoldOrderId { get; set; }
    public uint Version { get; set; }  // Maps to PostgreSQL xmin via IsRowVersion.
}

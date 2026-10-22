namespace TicketRush.Infrastructure;

/// <summary>
/// Inventory-owned evidence that an authorized hold became a sale.
/// OrderId refers to Ordering's independently owned order identifier; this row
/// is NOT a duplicate Ordering aggregate or part of the legacy orders table.
/// </summary>
public sealed class InventorySaleRecord
{
    public Guid OrderId { get; set; }
    public Guid HoldId { get; set; }
    public Guid BuyerId { get; set; }
    public DateTimeOffset SoldAtUtc { get; set; }
}

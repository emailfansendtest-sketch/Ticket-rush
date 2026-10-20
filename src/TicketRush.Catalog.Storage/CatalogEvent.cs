namespace TicketRush.Catalog.Storage;

/// <summary>Catalog-owned EF entity. Not shared with the Booking/Inventory database.</summary>
public sealed class CatalogEvent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset StartsAtUtc { get; set; }
}

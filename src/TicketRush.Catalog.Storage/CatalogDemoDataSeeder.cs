using Microsoft.EntityFrameworkCore;

namespace TicketRush.Catalog.Storage;

public static class CatalogDemoDataSeeder
{
    // Must correspond to Inventory's temporary local event reference during cutover.
    public static readonly Guid DemoEventId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static async Task SeedAsync(CatalogDbContext db, CancellationToken ct = default)
    {
        if (await db.Events.AnyAsync(x => x.Id == DemoEventId, ct))
            return;
        db.Events.Add(new CatalogEvent
        {
            Id = DemoEventId,
            Name = "Demo Concert",
            StartsAtUtc = new DateTimeOffset(2027, 4, 20, 18, 0, 0, TimeSpan.Zero)
        });
        await db.SaveChangesAsync(ct);
    }
}

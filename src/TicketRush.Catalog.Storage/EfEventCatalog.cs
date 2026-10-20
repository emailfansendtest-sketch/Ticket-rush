using Microsoft.EntityFrameworkCore;
using TicketRush.Catalog;

namespace TicketRush.Catalog.Storage;

/// <summary>Catalog's local EF-backed query. Never uses the Booking database.</summary>
public sealed class EfEventCatalog(CatalogDbContext db) : IEventCatalog
{
    public async Task<IReadOnlyList<EventSummary>> ListEventsAsync(CancellationToken ct) =>
        await db.Events.AsNoTracking()
            .OrderBy(x => x.StartsAtUtc)
            .ThenBy(x => x.Id)
            .Select(x => new EventSummary(x.Id, x.Name, x.StartsAtUtc))
            .ToArrayAsync(ct);
}

using Microsoft.EntityFrameworkCore;
using TicketRush.Infrastructure;

namespace TicketRush.Catalog;

/// <summary>Phase-1 Catalog query implementation, using the shared persistence layer.</summary>
public sealed class EfEventCatalog(BookingDbContext db) : IEventCatalog
{
    public async Task<IReadOnlyList<EventSummary>> ListEventsAsync(CancellationToken ct)
    {
        return await db.Events
            .AsNoTracking()
            .OrderBy(x => x.StartsAtUtc)
            .Select(x => new EventSummary(x.Id, x.Name, x.StartsAtUtc))
            .ToArrayAsync(ct);
    }
}

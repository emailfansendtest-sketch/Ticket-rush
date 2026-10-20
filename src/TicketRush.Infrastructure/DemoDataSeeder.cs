using Microsoft.EntityFrameworkCore;
using TicketRush.Domain;

namespace TicketRush.Infrastructure;

public static class DemoDataSeeder
{
    public static readonly Guid DemoEventId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    // For fresh development/test databases, not production event management.
    public static async Task SeedAsync(BookingDbContext db, CancellationToken ct = default)
    {
        if (await db.Events.AnyAsync(x => x.Id == DemoEventId, ct))
            return;

        db.Events.Add(new TicketEvent
        {
            Id = DemoEventId,
            Name = "Demo Concert",
            StartsAtUtc = new DateTimeOffset(2027, 4, 20, 18, 0, 0, TimeSpan.Zero)
        });

        db.Seats.AddRange(Enumerable.Range(1, 100).Select(i => new SeatAllocation
        {
            EventId = DemoEventId,
            SeatCode = $"A-{i:000}",
            Status = SeatStatus.Available
        }));

        await db.SaveChangesAsync(ct);
    }
}

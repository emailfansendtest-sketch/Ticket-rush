using Microsoft.EntityFrameworkCore;
using TicketRush.Inventory.Persistence;
using TicketRush.Domain;

var connection = Environment.GetEnvironmentVariable("ConnectionStrings__InventoryDb")
    ?? throw new InvalidOperationException("ConnectionStrings__InventoryDb is required.");
var demoEventId = Guid.Parse("11111111-1111-1111-1111-111111111111");
var options = new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(connection).Options;
await using var db = new InventoryDbContext(options);
await db.Database.MigrateAsync();
Console.WriteLine("Inventory database migrations applied.");
// Seed only when explicitly requested on fresh installations, never in a data cutover.
if (string.Equals(Environment.GetEnvironmentVariable("SEED_DEMO_DATA"), "true", StringComparison.OrdinalIgnoreCase))
{
    if (!await db.Events.AnyAsync(x => x.Id == demoEventId))
    {
        db.Events.Add(new TicketEvent
        {
            Id = demoEventId, Name = "Demo Concert",
            StartsAtUtc = new DateTimeOffset(2027, 4, 20, 18, 0, 0, TimeSpan.Zero)
        });
        db.Seats.AddRange(Enumerable.Range(1, 100).Select(i => new SeatAllocation
        {
            EventId = demoEventId, SeatCode = $"A-{i:000}", Status = SeatStatus.Available
        }));
        await db.SaveChangesAsync();
    }
}

// This ID matches the Catalog demo event and old Booking seeder.

using Microsoft.EntityFrameworkCore;
using TicketRush.Infrastructure;

var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__BookingDb")
    ?? throw new InvalidOperationException("ConnectionStrings__BookingDb must be provided.");

var options = new DbContextOptionsBuilder<BookingDbContext>()
    .UseNpgsql(connectionString)
    .Options;

await using var db = new BookingDbContext(options);

// Called once by a deployment job, never in every API replica.
await db.Database.MigrateAsync();
Console.WriteLine("TicketRush database migrations applied.");

if (string.Equals(Environment.GetEnvironmentVariable("SEED_DEMO_DATA"),
        "true", StringComparison.OrdinalIgnoreCase))
{
    await DemoDataSeeder.SeedAsync(db);
    Console.WriteLine("TicketRush demo data seeded (if absent).");
}

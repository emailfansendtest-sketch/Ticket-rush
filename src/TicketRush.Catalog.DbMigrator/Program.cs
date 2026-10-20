using Microsoft.EntityFrameworkCore;
using TicketRush.Catalog.Storage;

string connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__CatalogDb")
    ?? throw new InvalidOperationException("ConnectionStrings__CatalogDb must be provided.");
var options = new DbContextOptionsBuilder<CatalogDbContext>()
    .UseNpgsql(connectionString).Options;
await using var db = new CatalogDbContext(options);
await db.Database.MigrateAsync();
Console.WriteLine("Catalog migrations applied.");
if (string.Equals(Environment.GetEnvironmentVariable("SEED_DEMO_DATA"),
    "true", StringComparison.OrdinalIgnoreCase))
{
    await CatalogDemoDataSeeder.SeedAsync(db);
    Console.WriteLine("Catalog demo event seeded (if absent).");
}

using Microsoft.EntityFrameworkCore;
using TicketRush.Infrastructure;

namespace TicketRush.IntegrationTests;

internal static class TestDatabaseInitializer
{
    public static async Task InitializeAsync(
        DbContextOptions<BookingDbContext> options, CancellationToken ct = default)
    {
        await using var db = new BookingDbContext(options);
        await db.Database.MigrateAsync(ct);
        await DemoDataSeeder.SeedAsync(db, ct);
    }
}

using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.Infrastructure;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class MigrationsIntegrationTests
{
    [Fact]
    public async Task Migrations_create_schema_and_reapplication_preserves_seed_data()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;

        await TestDatabaseInitializer.InitializeAsync(options);
        await TestDatabaseInitializer.InitializeAsync(options);

        await using var db = new BookingDbContext(options);
        Assert.Single(await db.Events.AsNoTracking().ToArrayAsync());
        Assert.Equal(100, await db.Seats.CountAsync());
        Assert.Empty(await db.Holds.ToArrayAsync());
        Assert.Empty(await db.Orders.ToArrayAsync());
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public void Ef_model_matches_checked_in_migration_snapshot()
    {
        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused")
            .Options;
        using var db = new BookingDbContext(options);
        Assert.False(db.Database.HasPendingModelChanges());
    }
}

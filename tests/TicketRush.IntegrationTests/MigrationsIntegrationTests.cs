using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TicketRush.Domain;
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
    public async Task Upgrade_backfills_prior_distributed_sales_without_removing_legacy_orders()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;

        // Simulate an existing deployment with the previous migration applied.
        await using (var db = new BookingDbContext(options))
        {
            await db.GetService<IMigrator>()
                .MigrateAsync("20261021180000_InventoryResultDeliveryScheduling");
            await DemoDataSeeder.SeedAsync(db);

            var holdId = Guid.NewGuid();
            var buyerId = Guid.NewGuid();
            var orderId = Guid.NewGuid();
            db.Holds.Add(new SeatHold
            {
                Id = holdId,
                EventId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                BuyerId = buyerId,
                IdempotencyKey = "existing-distributed-hold",
                RequestFingerprint = new string('0', 64),
                SeatCodesCsv = "A-001", SeatCount = 1,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
                Status = HoldStatus.Confirmed
            });
            db.Orders.Add(new TicketOrder
            {
                Id = orderId, HoldId = holdId, BuyerId = buyerId,
                IdempotencyKey = $"distributed:{orderId:D}",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            var soldSeat = await db.Seats.SingleAsync(x => x.SeatCode == "A-001");
            soldSeat.Status = SeatStatus.Sold;
            soldSeat.HoldId = holdId;
            soldSeat.SoldOrderId = orderId;

            // Legacy synchronous checkout must NOT be backfilled as an
            // Inventory-owned distributed sale.
            var legacyHoldId = Guid.NewGuid();
            db.Holds.Add(new SeatHold
            {
                Id = legacyHoldId,
                EventId = DemoDataSeeder.DemoEventId,
                BuyerId = buyerId,
                IdempotencyKey = "legacy-synchronous-hold",
                RequestFingerprint = new string('1', 64),
                SeatCodesCsv = "A-002", SeatCount = 1,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
                Status = HoldStatus.Confirmed
            });
            db.Orders.Add(new TicketOrder
            {
                Id = Guid.NewGuid(), HoldId = legacyHoldId, BuyerId = buyerId,
                IdempotencyKey = "legacy-synchronous-order", CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using (var db = new BookingDbContext(options))
            await db.Database.MigrateAsync();

        await using (var db = new BookingDbContext(options))
        {
            var oldOrders = await db.Orders.AsNoTracking().ToArrayAsync();
            Assert.Equal(2, oldOrders.Length);
            var oldOrder = Assert.Single(oldOrders, x => x.IdempotencyKey.StartsWith("distributed:"));
            var sale = Assert.Single(await db.InventorySales.AsNoTracking().ToArrayAsync());
            Assert.Equal(oldOrder.Id, sale.OrderId);
            Assert.Equal(oldOrder.HoldId, sale.HoldId);
            Assert.Equal(oldOrder.BuyerId, sale.BuyerId);
            Assert.Equal(oldOrder.CreatedAtUtc, sale.SoldAtUtc);
        }
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

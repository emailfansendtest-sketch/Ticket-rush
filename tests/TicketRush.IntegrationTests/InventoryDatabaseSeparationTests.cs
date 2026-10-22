using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.Domain;
using TicketRush.Infrastructure;
using TicketRush.Inventory;
using TicketRush.Inventory.Persistence;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class InventoryDatabaseSeparationTests
{
    private static readonly Guid DemoEvent =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Inventory_migrations_create_an_independent_schema_without_legacy_orders()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await pg.StartAsync();
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(pg.GetConnectionString()).Options;
        await using var ctx = new InventoryDbContext(options);
        await ctx.Database.MigrateAsync();
        Assert.Empty(await ctx.Database.GetPendingMigrationsAsync());
        await using var db = new NpgsqlConnection(pg.GetConnectionString());
        await db.OpenAsync();
        // to_regclass(...) returns PostgreSQL regclass, which Npgsql cannot read as
        // System.Object. Return a boolean from SQL instead.
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('public.orders') IS NULL", db);
        var missingOrders = await command.ExecuteScalarAsync();
        Assert.True(Assert.IsType<bool>(missingOrders));
        Assert.Empty(await ctx.Events.ToListAsync());
    }

    [Fact]
    public async Task Maintenance_copy_preserves_sold_seats_inbox_outbox_and_retry_state()
    {
        await using var legacy = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await using var destination = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await legacy.StartAsync();
        await destination.StartAsync();
        var sourceOptions = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(legacy.GetConnectionString()).Options;
        await using (var db = new BookingDbContext(sourceOptions))
        {
            await db.Database.MigrateAsync();
            await DemoDataSeeder.SeedAsync(db);
        }
        var targetOptions = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseNpgsql(destination.GetConnectionString()).Options;
        await using (var db = new InventoryDbContext(targetOptions))
            await db.Database.MigrateAsync();

        var buyer = Guid.NewGuid();
        var order = Guid.NewGuid();
        var message = Guid.NewGuid();
        Guid holdId;
        await using (var db = new BookingDbContext(sourceOptions))
        {
            var inventory = new InventoryService(db, TimeProvider.System);
            var hold = await inventory.HoldAsync(DemoEvent, buyer, "cutover-test-hold", ["A-019"], CancellationToken.None);
            Assert.Equal(ResultKind.Created, hold.Kind);
            holdId = hold.Value!.Id;
        }
        await using (var db = new BookingDbContext(sourceOptions))
        {
            var inventory = new InventoryService(db, TimeProvider.System);
            var cmd = new InventorySaleRequestedV1(message, order, holdId, buyer, DateTimeOffset.UtcNow);
            Assert.Equal(InventoryCommandOutcome.Processed,
                await new InventorySaleCommandProcessor(db, inventory, TimeProvider.System)
                    .ProcessAsync(cmd, CancellationToken.None));
        }
        await InventoryDataCopier.CopyAsync(legacy.GetConnectionString(), destination.GetConnectionString());

        await using var target = new InventoryDbContext(targetOptions);
        var sale = Assert.Single(await target.InventorySales.AsNoTracking().ToListAsync());
        Assert.Equal(order, sale.OrderId);
        var receipt = Assert.Single(await target.InventoryInbox.AsNoTracking().ToListAsync());
        Assert.Equal(message, receipt.MessageId);
        var outgoing = Assert.Single(await target.InventoryResultOutbox.AsNoTracking().ToListAsync());
        Assert.Equal(receipt.ResultMessageId, outgoing.MessageId);
        Assert.Null(outgoing.PublishedAtUtc);
        var seat = await target.Seats.SingleAsync(x => x.EventId == DemoEvent && x.SeatCode == "A-019");
        Assert.Equal(SeatStatus.Sold, seat.Status);
        Assert.Equal(order, seat.SoldOrderId);
        Assert.Equal(HoldStatus.Confirmed, (await target.Holds.SingleAsync(x => x.Id == holdId)).Status);
        Assert.Equal(100, await target.Seats.CountAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await InventoryDataCopier.CopyAsync(legacy.GetConnectionString(), destination.GetConnectionString()));

        // Source remains intact and unchanged after successful copy.
        await using var verifySource = new BookingDbContext(sourceOptions);
        Assert.Single(await verifySource.InventorySales.ToListAsync());
        Assert.Single(await verifySource.InventoryInbox.ToListAsync());
    }
}

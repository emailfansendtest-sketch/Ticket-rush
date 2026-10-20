using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.Domain;
using TicketRush.Infrastructure;
using TicketRush.Inventory;
using TicketRush.IntegrationTests.Utilities;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class InventoryIntegrationTests
{
    [Fact]
    public async Task Seat_listing_returns_sorted_untracked_projection_with_current_hold_state()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();

        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await TestDatabaseInitializer.InitializeAsync(options);

        await using (var write = new BookingDbContext(options))
        {
            IInventoryService inventory = new InventoryService(write, TimeProvider.System);
            var result = await inventory.HoldAsync(DemoDataSeeder.DemoEventId,
                Guid.NewGuid(), "inventory-projection-test", ["A-002"], CancellationToken.None);
            Assert.Equal(ResultKind.Created, result.Kind);
            Assert.NotNull(result.Value);
        }

        await using var read = new BookingDbContext(options);
        IInventoryService reader = new InventoryService(read, TimeProvider.System);
        var seats = await reader.ListSeatsAsync(DemoDataSeeder.DemoEventId, CancellationToken.None);

        Assert.Equal(100, seats.Count);
        Assert.Equal("A-001", seats[0].SeatCode);
        Assert.Equal("A-100", seats[^1].SeatCode);
        var held = Assert.Single(seats, s => s.SeatCode == "A-002");
        Assert.Equal(SeatStatus.Held, held.Status);
        Assert.NotNull(held.HeldUntilUtc);
        Assert.Equal(99, seats.Count(s => s.Status == SeatStatus.Available));
        Assert.Empty(read.ChangeTracker.Entries<SeatAllocation>());
    }

    [Fact]
    public async Task Expired_seat_is_logically_available_at_expiry_boundary_without_db_mutation()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await TestDatabaseInitializer.InitializeAsync(options);

        var clock = new TimeProviderMock
        {
            CurrentTimeUtc = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)
        };
        HoldView hold;
        await using (var writer = new BookingDbContext(options))
        {
            var inventory = new InventoryService(writer, clock);
            var result = await inventory.HoldAsync(
                DemoDataSeeder.DemoEventId, Guid.NewGuid(),
                "expiry-read-projection", ["A-077"], CancellationToken.None);
            Assert.Equal(ResultKind.Created, result.Kind);
            hold = Assert.IsType<HoldView>(result.Value);
        }

        await using var db = new BookingDbContext(options);
        var reader = new InventoryService(db, clock);
        clock.CurrentTimeUtc = hold.ExpiresAtUtc.AddTicks(-1);
        var justBefore = await reader.ListSeatsAsync(
            DemoDataSeeder.DemoEventId, CancellationToken.None);
        Assert.Equal(SeatStatus.Held,
            Assert.Single(justBefore, x => x.SeatCode == "A-077").Status);

        clock.CurrentTimeUtc = hold.ExpiresAtUtc;
        var expired = await reader.ListSeatsAsync(
            DemoDataSeeder.DemoEventId, CancellationToken.None);
        var expiredSeat = Assert.Single(expired, x => x.SeatCode == "A-077");
        Assert.Equal(SeatStatus.Available, expiredSeat.Status);
        Assert.Null(expiredSeat.HeldUntilUtc);
        Assert.Empty(db.ChangeTracker.Entries<SeatAllocation>());

        await using var verification = new BookingDbContext(options);
        var persisted = await verification.Seats.AsNoTracking().SingleAsync(
            x => x.EventId == DemoDataSeeder.DemoEventId && x.SeatCode == "A-077");
        Assert.Equal(SeatStatus.Held, persisted.Status);
        Assert.Equal(hold.Id, persisted.HoldId);
    }

    [Fact]
    public void Infrastructure_must_not_depend_on_inventory_module()
    {
        var references = typeof(BookingDbContext).Assembly.GetReferencedAssemblies();
        string name = typeof(IInventoryService).Assembly.GetName().Name!;
        Assert.DoesNotContain(references, assembly => assembly.Name == name);
    }
}

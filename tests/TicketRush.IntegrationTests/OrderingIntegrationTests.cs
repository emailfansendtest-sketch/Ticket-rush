using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.Domain;
using TicketRush.Infrastructure;
using TicketRush.Inventory;
using TicketRush.Ordering;
using TicketRush.IntegrationTests.Utilities;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class OrderingIntegrationTests
{
    [Fact]
    public async Task Checkout_reuses_one_transaction_and_persists_a_consistent_order_and_seat_state()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await TestDatabaseInitializer.InitializeAsync(options);

        var buyer = Guid.NewGuid();
        HoldView hold;
        await using (var write = new BookingDbContext(options))
        {
            var inventory = new InventoryService(write, TimeProvider.System);
            var reserved = await inventory.HoldAsync(DemoDataSeeder.DemoEventId,
                buyer, "ordering-module-hold", ["A-001", "A-002"], CancellationToken.None);
            Assert.Equal(ResultKind.Created, reserved.Kind);
            hold = Assert.IsType<HoldView>(reserved.Value);
        }

        OrderView order;
        await using (var db = new BookingDbContext(options))
        {
            var inventory = new InventoryService(db, TimeProvider.System);
            IOrderingService ordering = new OrderingService(db, inventory);
            var result = await ordering.ConfirmAsync(hold.Id, buyer,
                "ordering-module-checkout", CancellationToken.None);
            Assert.Equal(ResultKind.Created, result.Kind);
            order = Assert.IsType<OrderView>(result.Value);
        }

        await using var verify = new BookingDbContext(options);
        var savedOrder = Assert.Single(await verify.Orders.AsNoTracking().ToListAsync());
        Assert.Equal(order.Id, savedOrder.Id);
        Assert.Equal(hold.Id, savedOrder.HoldId);
        Assert.Equal(buyer, savedOrder.BuyerId);

        var savedHold = await verify.Holds.AsNoTracking()
            .SingleAsync(x => x.Id == hold.Id);
        Assert.Equal(HoldStatus.Confirmed, savedHold.Status);

        var sold = await verify.Seats.AsNoTracking()
            .Where(x => x.HoldId == hold.Id)
            .OrderBy(x => x.SeatCode).ToListAsync();
        Assert.Equal(new[] {"A-001", "A-002"}, sold.Select(x => x.SeatCode));
        Assert.All(sold, seat =>
        {
            Assert.Equal(SeatStatus.Sold, seat.Status);
            Assert.Equal(order.Id, seat.SoldOrderId);
            Assert.Null(seat.HeldUntilUtc);
        });
    }

    [Fact]
    public async Task Inventory_checkout_preparation_does_not_commit_when_caller_rolls_back()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await TestDatabaseInitializer.InitializeAsync(options);

        var buyer = Guid.NewGuid();
        HoldView hold;
        await using (var db = new BookingDbContext(options))
        {
            var inventory = new InventoryService(db, TimeProvider.System);
            var result = await inventory.HoldAsync(DemoDataSeeder.DemoEventId,
                buyer, "rollback-preparation", ["A-005"], CancellationToken.None);
            Assert.Equal(ResultKind.Created, result.Kind);
            hold = Assert.IsType<HoldView>(result.Value);
        }

        await using (var db = new BookingDbContext(options))
        {
            var inventory = new InventoryService(db, TimeProvider.System);
            IInventoryCheckout checkout = inventory;
            var orderId = Guid.NewGuid();

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await checkout.PrepareCheckoutAsync(
                    hold.Id, buyer, orderId, CancellationToken.None);
            });

            await using var tx = await db.Database.BeginTransactionAsync();
            var prepared = await checkout.PrepareCheckoutAsync(
                hold.Id, buyer, orderId, CancellationToken.None);
            Assert.Equal(CheckoutPreparationStatus.Ready, prepared.Status);
            Assert.NotNull(prepared.CheckedAtUtc);
            // Explicitly save to test DB rollback as well as no hidden commit.
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        await using var verify = new BookingDbContext(options);
        Assert.Empty(await verify.Orders.AsNoTracking().ToListAsync());
        Assert.Equal(HoldStatus.Active, (await verify.Holds.AsNoTracking()
            .SingleAsync(x => x.Id == hold.Id)).Status);
        var seat = await verify.Seats.AsNoTracking().SingleAsync(
            x => x.EventId == DemoDataSeeder.DemoEventId && x.SeatCode == "A-005");
        Assert.Equal(SeatStatus.Held, seat.Status);
        Assert.Equal(hold.Id, seat.HoldId);
        Assert.Null(seat.SoldOrderId);
    }

    [Fact]
    public async Task Checkout_replay_returns_the_identical_timestamp_as_first_response()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await TestDatabaseInitializer.InitializeAsync(options);

        // Force a timestamp PostgreSQL cannot represent exactly at 100-ns precision.
        var clock = new TimeProviderMock
        {
            CurrentTimeUtc = new DateTimeOffset(
                2026, 10, 20, 12, 0, 0, TimeSpan.Zero).AddTicks(7)
        };
        var buyer = Guid.NewGuid();
        HoldView hold;
        await using (var db = new BookingDbContext(options))
        {
            var result = await new InventoryService(db, clock).HoldAsync(
                DemoDataSeeder.DemoEventId, buyer,
                "timestamp-hold", ["A-010"], CancellationToken.None);
            Assert.Equal(ResultKind.Created, result.Kind);
            hold = Assert.IsType<HoldView>(result.Value);
        }

        OrderView firstOrder;
        await using (var db = new BookingDbContext(options))
        {
            var result = await new OrderingService(db, new InventoryService(db, clock))
                .ConfirmAsync(hold.Id, buyer, "timestamp-checkout", CancellationToken.None);
            Assert.Equal(ResultKind.Created, result.Kind);
            firstOrder = Assert.IsType<OrderView>(result.Value);
        }

        await using (var db = new BookingDbContext(options))
        {
            var replay = await new OrderingService(db, new InventoryService(db, clock))
                .ConfirmAsync(hold.Id, buyer, "timestamp-checkout", CancellationToken.None);
            Assert.Equal(ResultKind.Replayed, replay.Kind);
            Assert.Equal(firstOrder, Assert.IsType<OrderView>(replay.Value));
        }

        Assert.Equal(0, firstOrder.CreatedAtUtc.Ticks % 10);
        await using var verify = new BookingDbContext(options);
        var saved = await verify.Orders.AsNoTracking().SingleAsync();
        Assert.Equal(firstOrder.CreatedAtUtc, saved.CreatedAtUtc);
    }

    [Fact]
    public async Task Concurrent_checkout_of_different_holds_with_same_buyer_key_rolls_back_loser()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await TestDatabaseInitializer.InitializeAsync(options);
        var buyer = Guid.NewGuid();

        async Task<HoldView> CreateHoldAsync(string key, string code)
        {
            await using var db = new BookingDbContext(options);
            var result = await new InventoryService(db, TimeProvider.System)
                .HoldAsync(DemoDataSeeder.DemoEventId, buyer, key,
                    [code], CancellationToken.None);
            Assert.Equal(ResultKind.Created, result.Kind);
            return Assert.IsType<HoldView>(result.Value);
        }

        var firstHold = await CreateHoldAsync("checkout-race-hold-1", "A-011");
        var secondHold = await CreateHoldAsync("checkout-race-hold-2", "A-012");

        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<OperationResult<OrderView>> CheckoutAsync(BookingDbContext db, Guid holdId)
        {
            await start.Task;
            return await new OrderingService(db, new InventoryService(db, TimeProvider.System))
                .ConfirmAsync(holdId, buyer, "one-checkout-key", CancellationToken.None);
        }

        await using var db1 = new BookingDbContext(options);
        await using var db2 = new BookingDbContext(options);
        var firstTask = CheckoutAsync(db1, firstHold.Id);
        var secondTask = CheckoutAsync(db2, secondHold.Id);
        start.SetResult(true);

        var results = await Task.WhenAll(firstTask, secondTask);
        Assert.Equal(1, results.Count(r => r.Kind == ResultKind.Created));
        Assert.Equal(1, results.Count(r => r.Kind == ResultKind.Conflict));

        bool firstWon = results[0].Kind == ResultKind.Created;
        var winningHold = firstWon ? firstHold : secondHold;
        var losingHold = firstWon ? secondHold : firstHold;

        await using var verify = new BookingDbContext(options);
        var order = Assert.Single(await verify.Orders.AsNoTracking().ToArrayAsync());
        Assert.Equal(winningHold.Id, order.HoldId);
        Assert.Equal(buyer, order.BuyerId);
        Assert.Equal("one-checkout-key", order.IdempotencyKey);

        var holds = await verify.Holds.AsNoTracking()
            .Where(x => x.Id == firstHold.Id || x.Id == secondHold.Id)
            .ToDictionaryAsync(x => x.Id);
        Assert.Equal(2, holds.Count);
        Assert.Equal(HoldStatus.Confirmed, holds[winningHold.Id].Status);
        Assert.Equal(HoldStatus.Active, holds[losingHold.Id].Status);

        var seats = await verify.Seats.AsNoTracking()
            .Where(x => x.EventId == DemoDataSeeder.DemoEventId &&
                        (x.SeatCode == "A-011" || x.SeatCode == "A-012"))
            .ToDictionaryAsync(x => x.SeatCode);
        Assert.Equal(2, seats.Count);
        var winningSeat = seats[firstWon ? "A-011" : "A-012"];
        var losingSeat = seats[firstWon ? "A-012" : "A-011"];
        Assert.Equal(SeatStatus.Sold, winningSeat.Status);
        Assert.Equal(winningHold.Id, winningSeat.HoldId);
        Assert.Equal(order.Id, winningSeat.SoldOrderId);
        Assert.Null(winningSeat.HeldUntilUtc);
        Assert.Equal(SeatStatus.Held, losingSeat.Status);
        Assert.Equal(losingHold.Id, losingSeat.HoldId);
        Assert.Null(losingSeat.SoldOrderId);
        Assert.Equal(losingHold.ExpiresAtUtc, losingSeat.HeldUntilUtc);
    }

    [Fact]
    public void Shared_infrastructure_does_not_depend_on_ordering_module()
    {
        var references = typeof(BookingDbContext).Assembly.GetReferencedAssemblies();
        string orderingAssembly = typeof(IOrderingService).Assembly.GetName().Name!;
        Assert.DoesNotContain(references, assembly => assembly.Name == orderingAssembly);
    }
}

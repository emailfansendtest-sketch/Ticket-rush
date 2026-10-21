using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.Domain;
using TicketRush.Infrastructure;
using TicketRush.Inventory;
using TicketRush.IntegrationTests.Utilities;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class InventorySaleCommandTests
{
    private static readonly Guid DemoEvent = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static async Task<DbContextOptions<BookingDbContext>> SetupAsync(PostgreSqlContainer pg)
    {
        await pg.StartAsync();
        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(pg.GetConnectionString()).Options;
        await TestDatabaseInitializer.InitializeAsync(options);
        return options;
    }

    private static async Task<(Guid HoldId, Guid BuyerId)> CreateHoldAsync(
        DbContextOptions<BookingDbContext> options, string seat)
    {
        var buyer = Guid.NewGuid();
        await using var db = new BookingDbContext(options);
        var result = await new InventoryService(db, TimeProvider.System).HoldAsync(
            DemoEvent, buyer, Guid.NewGuid().ToString("N"), [seat], CancellationToken.None);
        Assert.Equal(ResultKind.Created, result.Kind);
        return (result.Value!.Id, buyer);
    }

    private static InventorySaleCommandProcessor Processor(BookingDbContext db) =>
        new(db, new InventoryService(db, TimeProvider.System), TimeProvider.System);

    private static InventorySaleRequestedV1 Command(Guid hold, Guid buyer, Guid? order = null,
        Guid? message = null) => new(message ?? Guid.NewGuid(), order ?? Guid.NewGuid(),
            hold, buyer, DateTimeOffset.UtcNow);

    [Fact]
    public async Task Processing_command_atomically_sells_seat_and_stores_inbox_and_result_outbox()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var (hold, buyer) = await CreateHoldAsync(options, "A-010");
        var cmd = Command(hold, buyer);
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryCommandOutcome.Processed,
                await Processor(db).ProcessAsync(cmd, CancellationToken.None));

        await using var verify = new BookingDbContext(options);
        var order = Assert.Single(await verify.Orders.AsNoTracking().ToListAsync());
        Assert.Equal(cmd.OrderId, order.Id);
        Assert.Equal(hold, order.HoldId);
        Assert.Equal(buyer, order.BuyerId);
        var receipt = Assert.Single(await verify.InventoryInbox.AsNoTracking().ToListAsync());
        Assert.Equal(cmd.MessageId, receipt.MessageId);
        Assert.Equal(cmd.OrderId, receipt.OrderId);
        var result = Assert.Single(await verify.InventoryResultOutbox.AsNoTracking().ToListAsync());
        Assert.Equal(receipt.ResultMessageId, result.MessageId);
        Assert.Equal(nameof(InventorySaleCommittedV1), result.MessageType);
        var evt = JsonSerializer.Deserialize<InventorySaleCommittedV1>(result.PayloadJson)!;
        Assert.Equal(cmd.OrderId, evt.OrderId);
        Assert.Equal(receipt.ResultMessageId, evt.MessageId);
        var seat = Assert.Single(await verify.Seats.AsNoTracking()
            .Where(x => x.EventId == DemoEvent && x.SeatCode == "A-010").ToListAsync());
        Assert.Equal(SeatStatus.Sold, seat.Status);
        Assert.Equal(cmd.OrderId, seat.SoldOrderId);
        Assert.Equal(HoldStatus.Confirmed, (await verify.Holds.SingleAsync(x => x.Id == hold)).Status);
    }

    [Fact]
    public async Task Replayed_message_does_not_sell_again_or_emit_new_result()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var (hold, buyer) = await CreateHoldAsync(options, "A-011");
        var cmd = Command(hold, buyer);
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryCommandOutcome.Processed,
                await Processor(db).ProcessAsync(cmd, CancellationToken.None));
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryCommandOutcome.Duplicate,
                await Processor(db).ProcessAsync(cmd, CancellationToken.None));
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryCommandOutcome.Duplicate,
                await Processor(db).ProcessAsync(cmd with { MessageId = Guid.NewGuid() }, CancellationToken.None));
        await using var verify = new BookingDbContext(options);
        Assert.Single(await verify.InventoryInbox.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.InventoryResultOutbox.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.Orders.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Competing_orders_for_one_hold_cannot_both_succeed()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var (hold, buyer) = await CreateHoldAsync(options, "A-012");
        var first = Command(hold, buyer);
        var second = Command(hold, buyer);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<InventoryCommandOutcome> Run(InventorySaleRequestedV1 command)
        {
            await using var db = new BookingDbContext(options);
            await start.Task;
            return await Processor(db).ProcessAsync(command, CancellationToken.None);
        }
        var tasks = new[] { Run(first), Run(second) };
        start.SetResult(true);
        Assert.All(await Task.WhenAll(tasks), outcome => Assert.Equal(InventoryCommandOutcome.Processed, outcome));

        await using var verify = new BookingDbContext(options);
        var order = Assert.Single(await verify.Orders.AsNoTracking().ToArrayAsync());
        var events = await verify.InventoryResultOutbox.AsNoTracking().ToArrayAsync();
        Assert.Collection(events.OrderBy(x => x.MessageType, StringComparer.Ordinal),
            e => Assert.Equal(nameof(InventorySaleCommittedV1), e.MessageType),
            e => Assert.Equal(nameof(InventorySaleRejectedV1), e.MessageType));
        Assert.Equal(order.Id, events.Single(x => x.MessageType == nameof(InventorySaleCommittedV1)).OrderId);
        Assert.Equal(
            new[] { first.OrderId, second.OrderId }.OrderBy(x => x).ToArray(),
            (await verify.InventoryInbox.Select(x => x.OrderId).ToArrayAsync())
                .OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Missing_hold_is_rejected_and_replay_is_stable()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var cmd = Command(Guid.NewGuid(), Guid.NewGuid());
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryCommandOutcome.Processed,
                await Processor(db).ProcessAsync(cmd, CancellationToken.None));
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryCommandOutcome.Duplicate,
                await Processor(db).ProcessAsync(cmd, CancellationToken.None));
        await using var verify = new BookingDbContext(options);
        Assert.Empty(await verify.Orders.ToArrayAsync());
        var result = Assert.Single(await verify.InventoryResultOutbox.ToArrayAsync());
        Assert.Equal(nameof(InventorySaleRejectedV1), result.MessageType);
        var evt = JsonSerializer.Deserialize<InventorySaleRejectedV1>(result.PayloadJson)!;
        Assert.Equal("HoldNotFound", evt.ReasonCode);
    }

    [Fact]
    public async Task Failed_result_outbox_insert_rolls_back_sale_and_clears_change_tracker()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var (hold, buyer) = await CreateHoldAsync(options, "A-013");
        await using (var ddl = new BookingDbContext(options))
            await ddl.Database.ExecuteSqlRawAsync("""
                CREATE OR REPLACE FUNCTION reject_inventory_result() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'simulated inventory result failure'; END; $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_result BEFORE INSERT ON inventory_result_outbox
                FOR EACH ROW EXECUTE FUNCTION reject_inventory_result();
                """);
        var cmd = Command(hold, buyer);
        await using (var db = new BookingDbContext(options))
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                Processor(db).ProcessAsync(cmd, CancellationToken.None));
            Assert.Empty(db.ChangeTracker.Entries());
        }
        await using (var verify = new BookingDbContext(options))
        {
            Assert.Empty(await verify.InventoryInbox.ToArrayAsync());
            Assert.Empty(await verify.InventoryResultOutbox.ToArrayAsync());
            Assert.Empty(await verify.Orders.ToArrayAsync());
            Assert.Equal(HoldStatus.Active, (await verify.Holds.SingleAsync(x => x.Id == hold)).Status);
            Assert.Equal(SeatStatus.Held, (await verify.Seats.SingleAsync(
                x => x.EventId == DemoEvent && x.SeatCode == "A-013")).Status);
            await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_result ON inventory_result_outbox;");
        }
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryCommandOutcome.Processed,
                await Processor(db).ProcessAsync(cmd, CancellationToken.None));
    }

    [Fact]
    public async Task Expired_hold_yields_rejection_without_creating_a_sale()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var (hold, buyer) = await CreateHoldAsync(options, "A-015");
        var clock = new TimeProviderMock { CurrentTimeUtc = DateTimeOffset.UtcNow.AddMinutes(11) };
        var cmd = Command(hold, buyer);
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryCommandOutcome.Processed,
                await new InventorySaleCommandProcessor(db, new InventoryService(db, clock), clock)
                    .ProcessAsync(cmd, CancellationToken.None));
        await using var verify = new BookingDbContext(options);
        Assert.Empty(await verify.Orders.ToArrayAsync());
        var result = Assert.Single(await verify.InventoryResultOutbox.ToArrayAsync());
        Assert.Equal("HoldExpired",
            JsonSerializer.Deserialize<InventorySaleRejectedV1>(result.PayloadJson)!.ReasonCode);
        Assert.Equal(HoldStatus.Active, (await verify.Holds.SingleAsync(x => x.Id == hold)).Status);
    }

    [Fact]
    public async Task Crossing_two_existing_message_and_order_ids_is_detected_as_collision()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var (hold1, buyer1) = await CreateHoldAsync(options, "A-016");
        var (hold2, buyer2) = await CreateHoldAsync(options, "A-017");
        var first = Command(hold1, buyer1);
        var second = Command(hold2, buyer2);
        await using (var db = new BookingDbContext(options))
            await Processor(db).ProcessAsync(first, CancellationToken.None);
        await using (var db = new BookingDbContext(options))
            await Processor(db).ProcessAsync(second, CancellationToken.None);
        await using (var db = new BookingDbContext(options))
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Processor(db).ProcessAsync(second with { MessageId = first.MessageId }, CancellationToken.None));
    }

    [Fact]
    public async Task Reused_message_identifier_for_a_different_order_is_rejected()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var (hold, buyer) = await CreateHoldAsync(options, "A-014");
        var cmd = Command(hold, buyer);
        await using (var db = new BookingDbContext(options))
            await Processor(db).ProcessAsync(cmd, CancellationToken.None);
        await using (var db = new BookingDbContext(options))
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Processor(db).ProcessAsync(cmd with { OrderId = Guid.NewGuid() }, CancellationToken.None));
        await using var verify = new BookingDbContext(options);
        Assert.Single(await verify.InventoryInbox.ToArrayAsync());
    }
    [Fact]
    public async Task Duplicate_command_rearms_confirmed_sale_result_without_reselling()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var (hold, buyer) = await CreateHoldAsync(options, "A-018");
        var command = Command(hold, buyer);
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryCommandOutcome.Processed,
                await Processor(db).ProcessAsync(command, CancellationToken.None));

        Guid resultId;
        string originalPayload;
        await using (var db = new BookingDbContext(options))
        {
            var result = await db.InventoryResultOutbox.SingleAsync();
            resultId = result.MessageId;
            originalPayload = result.PayloadJson;
            result.PublishedAtUtc = DateTimeOffset.UtcNow;
            result.PublishAttempts = 1;
            await db.SaveChangesAsync();
        }

        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryCommandOutcome.Duplicate,
                await Processor(db).ProcessAsync(command, CancellationToken.None));

        await using (var db = new BookingDbContext(options))
        {
            var result = await db.InventoryResultOutbox.SingleAsync();
            Assert.Equal(resultId, result.MessageId);
            Assert.Equal(originalPayload, result.PayloadJson);
            Assert.Null(result.PublishedAtUtc);
            Assert.Equal(1, result.PublishAttempts);
            Assert.Null(result.NextAttemptAtUtc);

            var publisher = new CapturingResultPublisher();
            Assert.Equal(InventoryResultDispatchOutcome.Published,
                await new InventoryResultOutboxDispatcher(db, publisher, TimeProvider.System)
                    .DispatchNextAsync(CancellationToken.None));
            Assert.Equal(resultId, Assert.Single(publisher.MessageIds));
        }

        await using var verify = new BookingDbContext(options);
        Assert.Single(await verify.InventoryInbox.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.InventoryResultOutbox.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.Orders.AsNoTracking().ToArrayAsync());
        Assert.Equal(SeatStatus.Sold, (await verify.Seats.SingleAsync(x =>
            x.EventId == DemoEvent && x.SeatCode == "A-018")).Status);
        Assert.NotNull((await verify.InventoryResultOutbox.SingleAsync()).PublishedAtUtc);
    }

    [Fact]
    public async Task Replayed_rejection_with_same_order_and_new_command_id_rearms_same_result()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var command = Command(Guid.NewGuid(), Guid.NewGuid());
        await using (var db = new BookingDbContext(options))
            await Processor(db).ProcessAsync(command, CancellationToken.None);
        Guid resultId;
        await using (var db = new BookingDbContext(options))
        {
            var result = await db.InventoryResultOutbox.SingleAsync();
            resultId = result.MessageId;
            result.PublishedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryCommandOutcome.Duplicate,
                await Processor(db).ProcessAsync(command with { MessageId = Guid.NewGuid() },
                    CancellationToken.None));
        await using var verify = new BookingDbContext(options);
        var restored = await verify.InventoryResultOutbox.SingleAsync();
        Assert.Equal(resultId, restored.MessageId);
        Assert.Null(restored.PublishedAtUtc);
        Assert.Equal(nameof(InventorySaleRejectedV1), restored.MessageType);
        Assert.Single(await verify.InventoryInbox.ToArrayAsync());
        Assert.Empty(await verify.Orders.ToArrayAsync());
    }

    [Fact]
    public async Task Duplicate_does_not_override_a_pending_result_retry_schedule()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var command = Command(Guid.NewGuid(), Guid.NewGuid());
        await using (var db = new BookingDbContext(options))
            await Processor(db).ProcessAsync(command, CancellationToken.None);
        var nextAttempt = DateTimeOffset.UtcNow.AddMinutes(5);
        await using (var db = new BookingDbContext(options))
        {
            var result = await db.InventoryResultOutbox.SingleAsync();
            result.PublishAttempts = 3;
            result.NextAttemptAtUtc = nextAttempt;
            result.LastError = "broker unavailable";
            await db.SaveChangesAsync();
        }
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryCommandOutcome.Duplicate,
                await Processor(db).ProcessAsync(command, CancellationToken.None));
        await using var verify = new BookingDbContext(options);
        var unchanged = await verify.InventoryResultOutbox.SingleAsync();
        Assert.Null(unchanged.PublishedAtUtc);
        Assert.Equal(nextAttempt.ToUnixTimeSeconds(),
            unchanged.NextAttemptAtUtc!.Value.ToUnixTimeSeconds());
        Assert.Equal(3, unchanged.PublishAttempts);
        Assert.Equal("broker unavailable", unchanged.LastError);
    }

    [Fact]
    public async Task Invalid_duplicate_identity_cannot_rearm_a_published_result()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var command = Command(Guid.NewGuid(), Guid.NewGuid());
        await using (var db = new BookingDbContext(options))
            await Processor(db).ProcessAsync(command, CancellationToken.None);
        await using (var db = new BookingDbContext(options))
        {
            (await db.InventoryResultOutbox.SingleAsync()).PublishedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        await using (var db = new BookingDbContext(options))
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Processor(db).ProcessAsync(command with { BuyerId = Guid.NewGuid() },
                    CancellationToken.None));
        await using var verify = new BookingDbContext(options);
        Assert.NotNull((await verify.InventoryResultOutbox.SingleAsync()).PublishedAtUtc);
    }

    [Fact]
    public async Task Competing_duplicates_cannot_create_another_result_or_inbox_receipt()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var command = Command(Guid.NewGuid(), Guid.NewGuid());
        await using (var db = new BookingDbContext(options))
            await Processor(db).ProcessAsync(command, CancellationToken.None);
        await using (var db = new BookingDbContext(options))
        {
            (await db.InventoryResultOutbox.SingleAsync()).PublishedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<InventoryCommandOutcome> ReplayAsync()
        {
            await using var db = new BookingDbContext(options);
            await start.Task;
            return await Processor(db).ProcessAsync(command, CancellationToken.None);
        }
        var attempts = new[] { ReplayAsync(), ReplayAsync() };
        start.TrySetResult(true);
        Assert.All(await Task.WhenAll(attempts), outcome =>
            Assert.Equal(InventoryCommandOutcome.Duplicate, outcome));
        await using var verify = new BookingDbContext(options);
        Assert.Null((await verify.InventoryResultOutbox.SingleAsync()).PublishedAtUtc);
        Assert.Single(await verify.InventoryInbox.ToArrayAsync());
        Assert.Single(await verify.InventoryResultOutbox.ToArrayAsync());
        Assert.Empty(await verify.Orders.ToArrayAsync());
    }

    [Fact]
    public async Task Missing_result_is_not_silently_acknowledged_as_a_duplicate()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(pg);
        var command = Command(Guid.NewGuid(), Guid.NewGuid());
        await using (var db = new BookingDbContext(options))
            await Processor(db).ProcessAsync(command, CancellationToken.None);
        await using (var db = new BookingDbContext(options))
        {
            db.InventoryResultOutbox.Remove(await db.InventoryResultOutbox.SingleAsync());
            await db.SaveChangesAsync();
        }
        await using (var db = new BookingDbContext(options))
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                Processor(db).ProcessAsync(command, CancellationToken.None));
    }

    private sealed class CapturingResultPublisher : IInventoryResultPublisher
    {
        public List<Guid> MessageIds { get; } = [];
        public Task PublishAsync(InventoryResultOutbox message, CancellationToken ct)
        {
            MessageIds.Add(message.MessageId);
            return Task.CompletedTask;
        }
    }

}

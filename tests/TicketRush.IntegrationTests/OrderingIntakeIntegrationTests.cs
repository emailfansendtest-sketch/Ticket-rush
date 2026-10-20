using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.Ordering.Persistence;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class OrderingIntakeIntegrationTests
{
    private static readonly Guid SampleHold = Guid.Parse("bca0f69e-dc9a-4987-b5dd-9ca5c0033149");

    private static async Task<DbContextOptions<OrderingDbContext>> InitializeAsync(
        PostgreSqlContainer postgres)
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new OrderingDbContext(options);
        await db.Database.MigrateAsync();
        return options;
    }

    [Fact]
    public async Task Accepted_checkout_persists_pending_intent_and_one_outbox_fact_atomically()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitializeAsync(postgres);
        var buyerId = Guid.NewGuid();
        CheckoutAdmission result;
        await using (var db = new OrderingDbContext(options))
        {
            result = await new CheckoutIntakeService(db, TimeProvider.System)
                .AcceptAsync(SampleHold, buyerId, "accept-first", CancellationToken.None);
        }

        Assert.Equal(CheckoutAdmissionKind.Accepted, result.Kind);
        Assert.Equal(CheckoutState.Pending, result.State);
        Assert.NotNull(result.OrderId);
        Assert.NotNull(result.RequestedAtUtc);
        Assert.Equal(0, result.RequestedAtUtc.Value.Ticks % 10);

        await using var verify = new OrderingDbContext(options);
        var order = Assert.Single(await verify.OrderIntents.AsNoTracking().ToArrayAsync());
        var outbox = Assert.Single(await verify.OutboxMessages.AsNoTracking().ToArrayAsync());
        Assert.Equal(result.OrderId.Value, order.Id);
        Assert.Equal(SampleHold, order.HoldId);
        Assert.Equal(buyerId, order.BuyerId);
        Assert.Equal(CheckoutState.Pending, order.State);
        Assert.Equal(result.RequestedAtUtc, order.RequestedAtUtc);
        Assert.Equal(order.Id, outbox.OrderId);
        Assert.Equal(nameof(CheckoutRequestedV1), outbox.MessageType);
        Assert.Null(outbox.PublishedAtUtc);
        Assert.Equal(0, outbox.PublishAttempts);
        var message = JsonSerializer.Deserialize<CheckoutRequestedV1>(outbox.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(message);
        Assert.Equal(outbox.MessageId, message.MessageId);
        Assert.Equal(order.Id, message.OrderId);
        Assert.Equal(SampleHold, message.HoldId);
        Assert.Equal(buyerId, message.BuyerId);
        Assert.Equal(order.RequestedAtUtc, message.RequestedAtUtc);
    }

    [Fact]
    public async Task Reusing_same_buyer_key_and_hold_replays_existing_intent_without_new_outbox_row()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitializeAsync(postgres);
        var buyer = Guid.NewGuid();
        CheckoutAdmission first;
        await using (var db = new OrderingDbContext(options))
            first = await new CheckoutIntakeService(db, TimeProvider.System)
                .AcceptAsync(SampleHold, buyer, "repeat", CancellationToken.None);
        await using (var db = new OrderingDbContext(options))
        {
            var second = await new CheckoutIntakeService(db, TimeProvider.System)
                .AcceptAsync(SampleHold, buyer, "repeat", CancellationToken.None);
            Assert.Equal(CheckoutAdmissionKind.Replayed, second.Kind);
            Assert.Equal(first.OrderId, second.OrderId);
            Assert.Equal(first.RequestedAtUtc, second.RequestedAtUtc);
        }
        await using var verify = new OrderingDbContext(options);
        Assert.Single(await verify.OrderIntents.AsNoTracking().ToArrayAsync());
        Assert.Single(await verify.OutboxMessages.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Key_reused_for_different_hold_or_hold_reused_with_new_key_conflicts()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitializeAsync(postgres);
        var buyer = Guid.NewGuid();
        await using var db = new OrderingDbContext(options);
        var service = new CheckoutIntakeService(db, TimeProvider.System);
        var first = await service.AcceptAsync(SampleHold, buyer, "same-key", CancellationToken.None);
        Assert.Equal(CheckoutAdmissionKind.Accepted, first.Kind);
        var keyReuse = await service.AcceptAsync(Guid.NewGuid(), buyer, "same-key", CancellationToken.None);
        var holdReuse = await service.AcceptAsync(SampleHold, buyer, "different-key", CancellationToken.None);
        Assert.Equal(CheckoutAdmissionKind.Conflict, keyReuse.Kind);
        Assert.Equal(CheckoutAdmissionKind.Conflict, holdReuse.Kind);
        Assert.Single(await db.OrderIntents.AsNoTracking().ToArrayAsync());
        Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_same_buyer_key_is_atomic_whether_holds_match_or_not(bool differentHolds)
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitializeAsync(postgres);
        var buyer = Guid.NewGuid();
        var secondHold = differentHolds ? Guid.NewGuid() : SampleHold;
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<CheckoutAdmission> AcceptAsync(OrderingDbContext db, Guid hold)
        {
            await start.Task;
            return await new CheckoutIntakeService(db, TimeProvider.System)
                .AcceptAsync(hold, buyer, "racing-checkout", CancellationToken.None);
        }

        await using var db1 = new OrderingDbContext(options);
        await using var db2 = new OrderingDbContext(options);
        var one = AcceptAsync(db1, SampleHold);
        var two = AcceptAsync(db2, secondHold);
        start.SetResult(true);
        var result = await Task.WhenAll(one, two);
        Assert.Single(result, x => x.Kind == CheckoutAdmissionKind.Accepted);
        Assert.Single(result, x => x.Kind == (differentHolds
            ? CheckoutAdmissionKind.Conflict : CheckoutAdmissionKind.Replayed));
        if (!differentHolds)
            Assert.Equal(result[0].OrderId, result[1].OrderId);

        await using var verify = new OrderingDbContext(options);
        var order = Assert.Single(await verify.OrderIntents.AsNoTracking().ToArrayAsync());
        var message = Assert.Single(await verify.OutboxMessages.AsNoTracking().ToArrayAsync());
        Assert.Equal(order.Id, message.OrderId);
        Assert.Equal(CheckoutState.Pending, order.State);
        Assert.Equal(result.Single(x => x.Kind == CheckoutAdmissionKind.Accepted).OrderId, order.Id);
    }

    [Fact]
    public async Task Different_buyers_may_use_the_same_key_for_distinct_holds()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitializeAsync(postgres);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<CheckoutAdmission> AcceptAsync(OrderingDbContext db, Guid hold)
        {
            await start.Task;
            return await new CheckoutIntakeService(db, TimeProvider.System)
                .AcceptAsync(hold, Guid.NewGuid(), "same-key", CancellationToken.None);
        }
        await using var db1 = new OrderingDbContext(options);
        await using var db2 = new OrderingDbContext(options);
        var one = AcceptAsync(db1, SampleHold);
        var two = AcceptAsync(db2, Guid.NewGuid());
        start.SetResult(true);
        var result = await Task.WhenAll(one, two);
        Assert.All(result, x => Assert.Equal(CheckoutAdmissionKind.Accepted, x.Kind));
        Assert.NotEqual(result[0].OrderId, result[1].OrderId);
        await using var verify = new OrderingDbContext(options);
        Assert.Equal(2, await verify.OrderIntents.CountAsync());
        Assert.Equal(2, await verify.OutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Outbox_insert_failure_rolls_back_the_pending_order()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitializeAsync(postgres);

        await using (var setup = new OrderingDbContext(options))
        {
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_checkout_outbox() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'Injected outbox failure';
                END;
                $$ LANGUAGE plpgsql;
                """);
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_checkout_outbox_insert
                BEFORE INSERT ON outbox_messages
                FOR EACH ROW EXECUTE FUNCTION reject_checkout_outbox();
                """);
        }

        await using var db = new OrderingDbContext(options);
        var buyer = Guid.NewGuid();
        var service = new CheckoutIntakeService(db, TimeProvider.System);
        await Assert.ThrowsAsync<DbUpdateException>(() => service.AcceptAsync(
            SampleHold, buyer, "rolled-back", CancellationToken.None));

        await using (var verifyRollback = new OrderingDbContext(options))
        {
            Assert.Empty(await verifyRollback.OrderIntents.AsNoTracking().ToArrayAsync());
            Assert.Empty(await verifyRollback.OutboxMessages.AsNoTracking().ToArrayAsync());
        }

        // A failed SaveChanges leaves Added entities tracked unless explicitly
        // cleared. Verify that this service is safe to invoke again on the same
        // DbContext after the underlying database problem is corrected.
        Assert.Empty(db.ChangeTracker.Entries<OrderIntent>());
        Assert.Empty(db.ChangeTracker.Entries<OutboxMessage>());

        await db.Database.ExecuteSqlRawAsync(
            "DROP TRIGGER reject_checkout_outbox_insert ON outbox_messages;");

        var retry = await service.AcceptAsync(
            SampleHold, buyer, "rolled-back", CancellationToken.None);
        Assert.Equal(CheckoutAdmissionKind.Accepted, retry.Kind);

        await using var verifyRecovery = new OrderingDbContext(options);
        var recoveredOrder = Assert.Single(
            await verifyRecovery.OrderIntents.AsNoTracking().ToArrayAsync());
        var recoveredMessage = Assert.Single(
            await verifyRecovery.OutboxMessages.AsNoTracking().ToArrayAsync());
        Assert.Equal(recoveredOrder.Id, recoveredMessage.OrderId);
    }

    [Fact]
    public async Task Initial_ordering_migration_is_reentrant_and_has_no_booking_dependencies()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitializeAsync(postgres);
        await using var db = new OrderingDbContext(options);
        await db.Database.MigrateAsync();
        Assert.Single(await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.OrderIntents.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.OutboxMessages.AsNoTracking().ToArrayAsync());

        var referenced = typeof(OrderingDbContext).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(referenced, x => x.Name == "TicketRush.Infrastructure");
        Assert.DoesNotContain(referenced, x => x.Name == "TicketRush.Inventory");
        Assert.DoesNotContain(referenced, x => x.Name == "TicketRush.Domain");
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.Infrastructure;
using TicketRush.IntegrationTests.Utilities;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class InventoryResultOutboxDispatchTests
{
    private static async Task<DbContextOptions<BookingDbContext>> SetupAsync(PostgreSqlContainer postgres)
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await TestDatabaseInitializer.InitializeAsync(options);
        return options;
    }

    private static async Task<(Guid CommittedId, Guid RejectedId)> SeedBothAsync(
        DbContextOptions<BookingDbContext> options)
    {
        var committedId = Guid.NewGuid();
        var rejectedId = Guid.NewGuid();
        var committedOrderId = Guid.NewGuid();
        var rejectedOrderId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = new BookingDbContext(options);
        db.InventoryResultOutbox.AddRange(
            new InventoryResultOutbox
            {
                MessageId = committedId, OrderId = committedOrderId,
                MessageType = nameof(InventorySaleCommittedV1),
                PayloadJson = JsonSerializer.Serialize(new InventorySaleCommittedV1(
                    committedId, committedOrderId, Guid.NewGuid(), now)),
                CreatedAtUtc = now.AddSeconds(-2)
            },
            new InventoryResultOutbox
            {
                MessageId = rejectedId, OrderId = rejectedOrderId,
                MessageType = nameof(InventorySaleRejectedV1),
                PayloadJson = JsonSerializer.Serialize(new InventorySaleRejectedV1(
                    rejectedId, rejectedOrderId, Guid.NewGuid(), "HoldNotFound", now)),
                CreatedAtUtc = now.AddSeconds(-1)
            });
        await db.SaveChangesAsync();
        return (committedId, rejectedId);
    }

    private sealed class RecordingPublisher(Func<InventoryResultOutbox, CancellationToken, Task>? action = null)
        : IInventoryResultPublisher
    {
        public ConcurrentQueue<Guid> Sent { get; } = new();
        public async Task PublishAsync(InventoryResultOutbox message, CancellationToken ct)
        {
            Sent.Enqueue(message.MessageId);
            if (action is not null)
                await action(message, ct);
        }
    }

    [Fact]
    public async Task Both_result_types_are_published_once_and_marked_as_delivered()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(postgres);
        var (committedId, rejectedId) = await SeedBothAsync(options);
        var publisher = new RecordingPublisher();
        await using (var db = new BookingDbContext(options))
        {
            var dispatcher = new InventoryResultOutboxDispatcher(db, publisher, TimeProvider.System);
            Assert.Equal(InventoryResultDispatchOutcome.Published,
                await dispatcher.DispatchNextAsync(CancellationToken.None));
            Assert.Equal(InventoryResultDispatchOutcome.Published,
                await dispatcher.DispatchNextAsync(CancellationToken.None));
            Assert.Equal(InventoryResultDispatchOutcome.Empty,
                await dispatcher.DispatchNextAsync(CancellationToken.None));
        }
        Assert.Equal(new[] { committedId, rejectedId }, publisher.Sent.ToArray());
        await using var verify = new BookingDbContext(options);
        var messages = await verify.InventoryResultOutbox.AsNoTracking().ToArrayAsync();
        Assert.Equal(2, messages.Length);
        Assert.All(messages, m =>
        {
            Assert.NotNull(m.PublishedAtUtc);
            Assert.Equal(1, m.PublishAttempts);
            Assert.Null(m.NextAttemptAtUtc);
            Assert.Null(m.LastError);
            Assert.Equal(0, m.PublishedAtUtc.Value.Ticks % 10);
        });
    }

    [Fact]
    public async Task Failed_result_publish_is_rescheduled_then_retried_with_original_id()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(postgres);
        var (committedId, _) = await SeedBothAsync(options);
        var clock = new TimeProviderMock { CurrentTimeUtc = DateTimeOffset.UtcNow };
        var attempts = 0;
        var publisher = new RecordingPublisher((_, _) =>
            Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new IOException("broker is down"))
                : Task.CompletedTask);
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryResultDispatchOutcome.Deferred,
                await new InventoryResultOutboxDispatcher(db, publisher, clock)
                    .DispatchNextAsync(CancellationToken.None));
        await using (var db = new BookingDbContext(options))
        {
            var result = await db.InventoryResultOutbox.AsNoTracking()
                .SingleAsync(x => x.MessageId == committedId);
            Assert.Null(result.PublishedAtUtc);
            Assert.Equal(1, result.PublishAttempts);
            Assert.NotNull(result.LastError);
            Assert.Contains("broker is down", result.LastError);
            Assert.Equal(clock.CurrentTimeUtc.AddSeconds(2).ToUnixTimeSeconds(),
                result.NextAttemptAtUtc!.Value.ToUnixTimeSeconds());
        }
        // The second result is still due and must not be blocked by the first result's retry delay.
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryResultDispatchOutcome.Published,
                await new InventoryResultOutboxDispatcher(db, publisher, clock)
                    .DispatchNextAsync(CancellationToken.None));
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryResultDispatchOutcome.Empty,
                await new InventoryResultOutboxDispatcher(db, publisher, clock)
                    .DispatchNextAsync(CancellationToken.None));
        clock.CurrentTimeUtc = clock.CurrentTimeUtc.AddSeconds(3);
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryResultDispatchOutcome.Published,
                await new InventoryResultOutboxDispatcher(db, publisher, clock)
                    .DispatchNextAsync(CancellationToken.None));
        Assert.Equal(2, publisher.Sent.ToArray().Count(x => x == committedId));
        await using var verify = new BookingDbContext(options);
        var delivered = await verify.InventoryResultOutbox.AsNoTracking()
            .SingleAsync(x => x.MessageId == committedId);
        Assert.NotNull(delivered.PublishedAtUtc);
        Assert.Equal(2, delivered.PublishAttempts);
        Assert.Null(delivered.NextAttemptAtUtc);
        Assert.Null(delivered.LastError);
    }

    [Fact]
    public async Task Multiple_dispatchers_do_not_claim_one_result_concurrently()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(postgres);
        // Only one eligible result for this test.
        var id = Guid.NewGuid();
        await using (var seed = new BookingDbContext(options))
        {
            seed.InventoryResultOutbox.Add(new InventoryResultOutbox
            {
                MessageId = id, OrderId = Guid.NewGuid(),
                MessageType = nameof(InventorySaleRejectedV1),
                PayloadJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await seed.SaveChangesAsync();
        }
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new RecordingPublisher(async (_, ct) =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(ct);
        });
        await using var db1 = new BookingDbContext(options);
        await using var db2 = new BookingDbContext(options);
        var first = new InventoryResultOutboxDispatcher(db1, publisher, TimeProvider.System)
            .DispatchNextAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var second = await new InventoryResultOutboxDispatcher(db2, publisher, TimeProvider.System)
                .DispatchNextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(InventoryResultDispatchOutcome.Empty, second);
        }
        finally
        {
            release.TrySetResult(true);
        }
        Assert.Equal(InventoryResultDispatchOutcome.Published, await first);
        Assert.Equal(id, Assert.Single(publisher.Sent));
    }

    [Fact]
    public async Task Confirmation_followed_by_database_failure_leaves_row_pending_for_redelivery()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(postgres);
        var (firstId, _) = await SeedBothAsync(options);
        var publisher = new RecordingPublisher();
        await using (var setup = new BookingDbContext(options))
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_inventory_result_delivery() RETURNS trigger AS $$
                BEGIN
                    IF NEW.published_at_utc IS NOT NULL THEN
                        RAISE EXCEPTION 'simulated database failure after broker ACK';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_delivery_update BEFORE UPDATE ON inventory_result_outbox
                FOR EACH ROW EXECUTE FUNCTION reject_inventory_result_delivery();
                """);
        await using (var db = new BookingDbContext(options))
            await Assert.ThrowsAnyAsync<Exception>(() =>
                new InventoryResultOutboxDispatcher(db, publisher, TimeProvider.System)
                    .DispatchNextAsync(CancellationToken.None));
        await using (var verify = new BookingDbContext(options))
        {
            var pending = await verify.InventoryResultOutbox.AsNoTracking()
                .SingleAsync(x => x.MessageId == firstId);
            Assert.Null(pending.PublishedAtUtc);
            Assert.Equal(0, pending.PublishAttempts);
            await verify.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER reject_delivery_update ON inventory_result_outbox;");
            await verify.Database.ExecuteSqlRawAsync(
                "DROP FUNCTION reject_inventory_result_delivery();");
        }
        await using (var db = new BookingDbContext(options))
            Assert.Equal(InventoryResultDispatchOutcome.Published,
                await new InventoryResultOutboxDispatcher(db, publisher, TimeProvider.System)
                    .DispatchNextAsync(CancellationToken.None));
        Assert.Equal(new[] { firstId, firstId }, publisher.Sent.ToArray());
    }

    [Fact]
    public async Task Pending_result_is_eligible_after_second_migration_without_data_loss()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(postgres);
        await SeedBothAsync(options);
        await using var db = new BookingDbContext(options);
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains("20261021040000_InventoryInboxAndOutbox", applied);
        Assert.Contains("20261021180000_InventoryResultDeliveryScheduling", applied);
        Assert.All(await db.InventoryResultOutbox.ToArrayAsync(), message =>
        {
            Assert.Equal(0, message.PublishAttempts);
            Assert.Null(message.NextAttemptAtUtc);
            Assert.Null(message.PublishedAtUtc);
            Assert.Null(message.LastError);
        });
    }
}

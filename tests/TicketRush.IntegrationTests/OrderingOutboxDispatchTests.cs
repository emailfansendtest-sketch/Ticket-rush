using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.IntegrationTests.Utilities;
using TicketRush.Ordering.Persistence;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class OrderingOutboxDispatchTests
{
    private static async Task<DbContextOptions<OrderingDbContext>> InitializeAsync(
        PostgreSqlContainer postgres)
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new OrderingDbContext(options);
        await db.Database.MigrateAsync();
        var migrations = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(migrations, x => x.EndsWith("_OrderIntakeInitialCreate", StringComparison.Ordinal));
        Assert.Contains(migrations, x => x.EndsWith("_OutboxDeliveryScheduling", StringComparison.Ordinal));
        return options;
    }

    private static async Task<(Guid Id, Guid MessageId)> SeedAsync(
        DbContextOptions<OrderingDbContext> options, string key)
    {
        await using var db = new OrderingDbContext(options);
        var result = await new CheckoutIntakeService(db, TimeProvider.System)
            .AcceptAsync(Guid.NewGuid(), Guid.NewGuid(), key, CancellationToken.None);
        Assert.Equal(CheckoutAdmissionKind.Accepted, result.Kind);
        return (result.OrderId!.Value,
            Assert.Single(await db.OutboxMessages.AsNoTracking().Where(x => x.OrderId == result.OrderId).ToArrayAsync()).MessageId);
    }

    private sealed class RecordingPublisher(Func<OutboxMessage, CancellationToken, Task>? action = null)
        : IOutboxPublisher
    {
        public ConcurrentQueue<Guid> PublishedIds { get; } = new();
        public async Task PublishAsync(OutboxMessage message, CancellationToken ct)
        {
            PublishedIds.Enqueue(message.MessageId);
            if (action is not null)
                await action(message, ct);
        }
    }

    [Fact]
    public async Task Confirmed_publish_marks_only_the_matching_outbox_row_and_keeps_payload()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitializeAsync(postgres);
        var (orderId, messageId) = await SeedAsync(options, "published-once");
        var now = DateTimeOffset.UtcNow;
        var clock = new TimeProviderMock { CurrentTimeUtc = now };
        var publisher = new RecordingPublisher();

        await using (var db = new OrderingDbContext(options))
        {
            var dispatcher = new OutboxDispatcher(db, publisher, clock);
            Assert.Equal(DispatchOutcome.Published, await dispatcher.DispatchNextAsync(CancellationToken.None));
            Assert.Equal(DispatchOutcome.Empty, await dispatcher.DispatchNextAsync(CancellationToken.None));
        }

        await using var verify = new OrderingDbContext(options);
        var outbox = Assert.Single(await verify.OutboxMessages.AsNoTracking().ToArrayAsync());
        Assert.Equal(orderId, outbox.OrderId);
        Assert.Equal(messageId, outbox.MessageId);
        Assert.Equal(messageId, Assert.Single(publisher.PublishedIds));
        Assert.Equal(1, outbox.PublishAttempts);
        Assert.NotNull(outbox.PublishedAtUtc);
        Assert.Equal(0, outbox.PublishedAtUtc.Value.Ticks % 10);
        Assert.Null(outbox.NextAttemptAtUtc);
        Assert.Null(outbox.LastError);
        Assert.Contains(messageId.ToString("D"), outbox.PayloadJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failed_publish_is_deferred_and_retried_with_the_same_message_id()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitializeAsync(postgres);
        var (_, messageId) = await SeedAsync(options, "retry");
        int calls = 0;
        var clock = new TimeProviderMock { CurrentTimeUtc = DateTimeOffset.UtcNow };
        var publisher = new RecordingPublisher((_, _) =>
            Interlocked.Increment(ref calls) == 1
                ? Task.FromException(new IOException("broker offline"))
                : Task.CompletedTask);

        await using (var db = new OrderingDbContext(options))
            Assert.Equal(DispatchOutcome.Deferred,
                await new OutboxDispatcher(db, publisher, clock).DispatchNextAsync(CancellationToken.None));

        await using (var verify = new OrderingDbContext(options))
        {
            var pending = Assert.Single(await verify.OutboxMessages.AsNoTracking().ToArrayAsync());
            Assert.Null(pending.PublishedAtUtc);
            Assert.Equal(1, pending.PublishAttempts);
            Assert.Equal(clock.CurrentTimeUtc.AddSeconds(2).ToUnixTimeSeconds(),
                pending.NextAttemptAtUtc!.Value.ToUnixTimeSeconds());
            Assert.NotNull(pending.LastError);
            Assert.Contains("broker offline", pending.LastError);
        }

        await using (var db = new OrderingDbContext(options))
            Assert.Equal(DispatchOutcome.Empty,
                await new OutboxDispatcher(db, publisher, clock).DispatchNextAsync(CancellationToken.None));

        clock.CurrentTimeUtc = clock.CurrentTimeUtc.AddSeconds(3);
        await using (var db = new OrderingDbContext(options))
            Assert.Equal(DispatchOutcome.Published,
                await new OutboxDispatcher(db, publisher, clock).DispatchNextAsync(CancellationToken.None));

        Assert.Equal(new[] { messageId, messageId }, publisher.PublishedIds.ToArray());
        await using var finalDb = new OrderingDbContext(options);
        var sent = Assert.Single(await finalDb.OutboxMessages.AsNoTracking().ToArrayAsync());
        Assert.Equal(2, sent.PublishAttempts);
        Assert.NotNull(sent.PublishedAtUtc);
        Assert.Null(sent.LastError);
    }

    [Fact]
    public async Task Two_dispatchers_cannot_publish_the_same_row_at_the_same_time()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitializeAsync(postgres);
        await SeedAsync(options, "locked");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new RecordingPublisher(async (_, ct) =>
        {
            entered.TrySetResult(true);
            await release.Task.WaitAsync(ct);
        });

        await using var db1 = new OrderingDbContext(options);
        await using var db2 = new OrderingDbContext(options);
        var publishing = new OutboxDispatcher(db1, publisher, TimeProvider.System)
            .DispatchNextAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // PostgreSQL skips the locked row instead of waiting behind publisher.
        var another = await new OutboxDispatcher(db2, publisher, TimeProvider.System)
            .DispatchNextAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(DispatchOutcome.Empty, another);
        release.SetResult(true);
        Assert.Equal(DispatchOutcome.Published, await publishing);
        Assert.Single(publisher.PublishedIds);
    }

    [Fact]
    public async Task Broker_ack_followed_by_database_failure_may_redeliver_same_message()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitializeAsync(postgres);
        var (_, messageId) = await SeedAsync(options, "ack-then-db-fail");
        var publisher = new RecordingPublisher();

        await using (var setup = new OrderingDbContext(options))
        {
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_outbox_delivery_update() RETURNS trigger AS $$
                BEGIN
                    IF NEW.published_at_utc IS NOT NULL THEN
                        RAISE EXCEPTION 'simulated persistence failure';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_outbox_delivery BEFORE UPDATE ON outbox_messages
                    FOR EACH ROW EXECUTE FUNCTION fail_outbox_delivery_update();
                """);
        }

        await using (var db = new OrderingDbContext(options))
            await Assert.ThrowsAnyAsync<Exception>(() =>
                new OutboxDispatcher(db, publisher, TimeProvider.System)
                    .DispatchNextAsync(CancellationToken.None));

        await using (var verify = new OrderingDbContext(options))
        {
            var pending = Assert.Single(await verify.OutboxMessages.AsNoTracking().ToArrayAsync());
            Assert.Null(pending.PublishedAtUtc);
            Assert.Equal(0, pending.PublishAttempts); // DB transaction rolled back
            await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_outbox_delivery ON outbox_messages;");
            await verify.Database.ExecuteSqlRawAsync("DROP FUNCTION fail_outbox_delivery_update();");
        }

        await using (var db = new OrderingDbContext(options))
            Assert.Equal(DispatchOutcome.Published,
                await new OutboxDispatcher(db, publisher, TimeProvider.System)
                    .DispatchNextAsync(CancellationToken.None));
        Assert.Equal(new[] { messageId, messageId }, publisher.PublishedIds.ToArray());
    }
}

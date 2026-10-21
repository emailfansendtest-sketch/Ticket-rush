using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.Ordering.Persistence;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class OrderingSagaRecoveryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly SagaRecoveryOptions Settings = new(
        TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), 3);

    private sealed class ManualClock(DateTimeOffset time) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = time;
        public override DateTimeOffset GetUtcNow() => Current;
    }

    private static async Task<DbContextOptions<OrderingDbContext>> SetupAsync(PostgreSqlContainer postgres)
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new OrderingDbContext(options);
        await db.Database.MigrateAsync();
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, x => x.EndsWith("_OrderingSagaRecovery", StringComparison.Ordinal));
        return options;
    }

    private static async Task<CheckoutRequestedV1> AdmitAsync(
        DbContextOptions<OrderingDbContext> options)
    {
        await using var db = new OrderingDbContext(options);
        var result = await new CheckoutIntakeService(db, new ManualClock(T0))
            .AcceptAsync(Guid.NewGuid(), Guid.NewGuid(), "recovery-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None);
        Assert.Equal(CheckoutAdmissionKind.Accepted, result.Kind);
        var fact = Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync());
        return JsonSerializer.Deserialize<CheckoutRequestedV1>(fact.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static async Task PublishFactAsync(DbContextOptions<OrderingDbContext> options,
        Guid messageId)
    {
        await using var db = new OrderingDbContext(options);
        var fact = await db.OutboxMessages.SingleAsync(x => x.MessageId == messageId);
        fact.PublishedAtUtc = T0.AddSeconds(1);
        await db.SaveChangesAsync();
    }

    private static async Task<SagaRecoveryOutcome> RecoverAsync(
        DbContextOptions<OrderingDbContext> options, ManualClock clock,
        SagaRecoveryOptions? settings = null)
    {
        await using var db = new OrderingDbContext(options);
        return await new SagaRecoveryDispatcher(db, clock, settings ?? Settings)
            .RecoverNextAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Fresh_pending_checkout_is_not_recovered()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(postgres);
        var fact = await AdmitAsync(options);
        var clock = new ManualClock(T0.AddSeconds(60));
        Assert.Equal(SagaRecoveryOutcome.None, await RecoverAsync(options, clock));
        await using var db = new OrderingDbContext(options);
        Assert.Equal(0, (await db.OrderIntents.SingleAsync(x => x.Id == fact.OrderId)).RecoveryAttempts);
    }

    [Fact]
    public async Task Overdue_checkout_requeues_same_fact_without_changing_business_state()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(postgres);
        var fact = await AdmitAsync(options);
        await PublishFactAsync(options, fact.MessageId);
        var clock = new ManualClock(T0.AddMinutes(3));
        Assert.Equal(SagaRecoveryOutcome.CheckoutRepublishScheduled,
            await RecoverAsync(options, clock));
        await using var db = new OrderingDbContext(options);
        var original = Assert.Single(await db.OutboxMessages.ToArrayAsync());
        Assert.Equal(fact.MessageId, original.MessageId);
        Assert.Null(original.PublishedAtUtc);
        Assert.Null(original.NextAttemptAtUtc);
        var order = await db.OrderIntents.SingleAsync();
        Assert.Equal(CheckoutState.Pending, order.State);
        Assert.Equal(1, order.RecoveryAttempts);
        Assert.Equal(clock.Current.AddSeconds(30), order.NextRecoveryAtUtc);
        Assert.False(order.RecoveryNeedsAttention);
    }

    [Fact]
    public async Task Overdue_checkout_requeues_inventory_command_instead_of_original_fact()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(postgres);
        var fact = await AdmitAsync(options);
        await using (var db = new OrderingDbContext(options))
            Assert.Equal(SagaProcessOutcome.Processed, await new OrderingSagaProcessor(db,
                new ManualClock(T0)).ScheduleInventoryAsync(fact, CancellationToken.None));
        Guid commandId;
        await using (var db = new OrderingDbContext(options))
        {
            var command = await db.OutboxMessages.SingleAsync(x =>
                x.MessageType == nameof(InventorySaleRequestedV1));
            commandId = command.MessageId;
            command.PublishedAtUtc = T0.AddSeconds(2);
            await db.SaveChangesAsync();
        }
        Assert.Equal(SagaRecoveryOutcome.InventoryRepublishScheduled,
            await RecoverAsync(options, new ManualClock(T0.AddMinutes(3))));
        await using var verify = new OrderingDbContext(options);
        Assert.Null((await verify.OutboxMessages.SingleAsync(x => x.MessageId == commandId)).PublishedAtUtc);
        Assert.Equal(2, await verify.OutboxMessages.CountAsync());
        Assert.Equal(CheckoutState.Pending, (await verify.OrderIntents.SingleAsync()).State);
    }

    [Fact]
    public async Task Already_queued_message_keeps_its_publisher_retry_schedule()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(postgres);
        var fact = await AdmitAsync(options);
        var publisherRetry = T0.AddHours(1);
        await using (var db = new OrderingDbContext(options))
        {
            var outbox = await db.OutboxMessages.SingleAsync(x => x.MessageId == fact.MessageId);
            outbox.NextAttemptAtUtc = publisherRetry;
            await db.SaveChangesAsync();
        }
        Assert.Equal(SagaRecoveryOutcome.CheckoutAlreadyQueued,
            await RecoverAsync(options, new ManualClock(T0.AddMinutes(3))));
        await using var verify = new OrderingDbContext(options);
        Assert.Equal(publisherRetry, (await verify.OutboxMessages.SingleAsync()).NextAttemptAtUtc);
    }

    [Fact]
    public async Task Retry_backoff_escalates_without_premature_failed_checkout()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(postgres);
        var fact = await AdmitAsync(options);
        await PublishFactAsync(options, fact.MessageId);
        var clock = new ManualClock(T0.AddMinutes(3));
        Assert.Equal(SagaRecoveryOutcome.CheckoutRepublishScheduled,
            await RecoverAsync(options, clock));
        Assert.Equal(SagaRecoveryOutcome.None, await RecoverAsync(options, clock));
        clock.Current = clock.Current.AddSeconds(30);
        Assert.Equal(SagaRecoveryOutcome.CheckoutAlreadyQueued,
            await RecoverAsync(options, clock));
        clock.Current = clock.Current.AddSeconds(60);
        Assert.Equal(SagaRecoveryOutcome.NeedsAttention, await RecoverAsync(options, clock));
        Assert.Equal(SagaRecoveryOutcome.None, await RecoverAsync(options, clock));
        await using var db = new OrderingDbContext(options);
        var order = await db.OrderIntents.SingleAsync();
        Assert.Equal(CheckoutState.Pending, order.State);
        Assert.True(order.RecoveryNeedsAttention);
        Assert.Equal(3, order.RecoveryAttempts);
        Assert.Null(order.NextRecoveryAtUtc);
        var status = await new CheckoutStatusReader(db).GetAsync(fact.OrderId, fact.BuyerId,
            CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal("Pending", status.State);
        Assert.True(status.RecoveryNeedsAttention);
    }

    [Fact]
    public async Task Late_inventory_result_clears_attention_and_finishes_order()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(postgres);
        var fact = await AdmitAsync(options);
        await using (var db = new OrderingDbContext(options))
            await new OrderingSagaProcessor(db, new ManualClock(T0))
                .ScheduleInventoryAsync(fact, CancellationToken.None);
        var instantAlert = new SagaRecoveryOptions(TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 1);
        Assert.Equal(SagaRecoveryOutcome.NeedsAttention,
            await RecoverAsync(options, new ManualClock(T0.AddMinutes(3)), instantAlert));
        var result = new InventorySaleCommittedV1(Guid.NewGuid(), fact.OrderId, fact.HoldId,
            T0.AddMinutes(4));
        await using (var db = new OrderingDbContext(options))
            Assert.Equal(SagaProcessOutcome.Processed,
                await new OrderingSagaProcessor(db, new ManualClock(T0.AddMinutes(4)))
                    .RecordResultAsync(result, CancellationToken.None));
        await using var verify = new OrderingDbContext(options);
        var order = await verify.OrderIntents.SingleAsync();
        Assert.Equal(CheckoutState.Completed, order.State);
        Assert.False(order.RecoveryNeedsAttention);
        Assert.Null(order.NextRecoveryAtUtc);
        Assert.Equal(SagaRecoveryOutcome.None,
            await RecoverAsync(options, new ManualClock(T0.AddMinutes(6)), instantAlert));
    }

    [Fact]
    public async Task Multiple_watchdogs_recover_a_single_overdue_order_once()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(postgres);
        var fact = await AdmitAsync(options);
        await PublishFactAsync(options, fact.MessageId);
        var clock = new ManualClock(T0.AddMinutes(3));
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => RecoverAsync(options, clock)));
        Assert.Single(outcomes, x => x == SagaRecoveryOutcome.CheckoutRepublishScheduled);
        Assert.Equal(7, outcomes.Count(x => x == SagaRecoveryOutcome.None));
        await using var verify = new OrderingDbContext(options);
        Assert.Equal(1, (await verify.OrderIntents.SingleAsync()).RecoveryAttempts);
    }

    [Fact]
    public async Task Missing_durable_fact_flags_attention_without_blocking_other_orders()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await SetupAsync(postgres);
        var fact = await AdmitAsync(options);
        await using (var db = new OrderingDbContext(options))
        {
            var message = await db.OutboxMessages.SingleAsync(x => x.MessageId == fact.MessageId);
            db.OutboxMessages.Remove(message);
            await db.SaveChangesAsync();
        }
        Assert.Equal(SagaRecoveryOutcome.NeedsAttention,
            await RecoverAsync(options, new ManualClock(T0.AddMinutes(3))));
        await using var verify = new OrderingDbContext(options);
        Assert.True((await verify.OrderIntents.SingleAsync()).RecoveryNeedsAttention);
        Assert.Equal(CheckoutState.Pending, (await verify.OrderIntents.SingleAsync()).State);
        Assert.Equal(SagaRecoveryOutcome.None,
            await RecoverAsync(options, new ManualClock(T0.AddMinutes(5))));
    }

}

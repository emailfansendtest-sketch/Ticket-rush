using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.Ordering.Persistence;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class OrderingSagaIntegrationTests
{
    private static async Task<DbContextOptions<OrderingDbContext>> InitAsync(PostgreSqlContainer postgres)
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new OrderingDbContext(options);
        await db.Database.MigrateAsync();
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, x => x.EndsWith("_OrderingSagaResultInbox", StringComparison.Ordinal));
        return options;
    }

    private static async Task<CheckoutRequestedV1> SeedAsync(
        DbContextOptions<OrderingDbContext> options)
    {
        await using var db = new OrderingDbContext(options);
        var accepted = await new CheckoutIntakeService(db, TimeProvider.System)
            .AcceptAsync(Guid.NewGuid(), Guid.NewGuid(), "saga-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None);
        Assert.Equal(CheckoutAdmissionKind.Accepted, accepted.Kind);
        var fact = Assert.Single(await db.OutboxMessages.AsNoTracking()
            .Where(x => x.OrderId == accepted.OrderId).ToArrayAsync());
        return JsonSerializer.Deserialize<CheckoutRequestedV1>(fact.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private static Task<SagaProcessOutcome> ScheduleAsync(
        DbContextOptions<OrderingDbContext> options, CheckoutRequestedV1 fact) =>
        UseAsync(options, saga => saga.ScheduleInventoryAsync(fact, CancellationToken.None));

    private static async Task<T> UseAsync<T>(DbContextOptions<OrderingDbContext> options,
        Func<OrderingSagaProcessor, Task<T>> action)
    {
        await using var db = new OrderingDbContext(options);
        return await action(new OrderingSagaProcessor(db, TimeProvider.System));
    }

    [Fact]
    public async Task Checkout_fact_creates_exactly_one_inventory_command_with_matching_identity()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitAsync(postgres);
        var fact = await SeedAsync(options);
        Assert.Equal(SagaProcessOutcome.Processed, await ScheduleAsync(options, fact));
        Assert.Equal(SagaProcessOutcome.Duplicate, await ScheduleAsync(options, fact));
        await using var db = new OrderingDbContext(options);
        var rows = await db.OutboxMessages.AsNoTracking()
            .Where(x => x.OrderId == fact.OrderId).ToArrayAsync();
        Assert.Equal(2, rows.Length);
        var commandOutbox = Assert.Single(rows, x => x.MessageType == nameof(InventorySaleRequestedV1));
        var command = JsonSerializer.Deserialize<InventorySaleRequestedV1>(commandOutbox.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(commandOutbox.MessageId, command.MessageId);
        Assert.Equal(fact.OrderId, command.OrderId);
        Assert.Equal(fact.HoldId, command.HoldId);
        Assert.Equal(fact.BuyerId, command.BuyerId);
        Assert.Equal(fact.RequestedAtUtc, command.RequestedAtUtc);
        Assert.Equal(CheckoutState.Pending,
            (await db.OrderIntents.SingleAsync(x => x.Id == fact.OrderId)).State);
    }

    [Fact]
    public async Task Committed_result_is_durable_and_duplicate_does_not_change_terminal_state()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitAsync(postgres);
        var fact = await SeedAsync(options);
        await ScheduleAsync(options, fact);
        var result = new InventorySaleCommittedV1(Guid.NewGuid(), fact.OrderId, fact.HoldId,
            DateTimeOffset.UtcNow);
        Assert.Equal(SagaProcessOutcome.Processed, await UseAsync(options, saga =>
            saga.RecordResultAsync(result, CancellationToken.None)));
        Assert.Equal(SagaProcessOutcome.Duplicate, await UseAsync(options, saga =>
            saga.RecordResultAsync(result, CancellationToken.None)));
        await using var db = new OrderingDbContext(options);
        Assert.Equal(CheckoutState.Completed,
            (await db.OrderIntents.SingleAsync(x => x.Id == fact.OrderId)).State);
        Assert.Equal(result.MessageId, Assert.Single(await db.ResultInbox.ToArrayAsync()).MessageId);
    }

    [Fact]
    public async Task Rejected_result_marks_order_failed_and_keeps_reason()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitAsync(postgres);
        var fact = await SeedAsync(options);
        await ScheduleAsync(options, fact);
        var result = new InventorySaleRejectedV1(Guid.NewGuid(), fact.OrderId, fact.HoldId,
            "HoldExpired", DateTimeOffset.UtcNow);
        Assert.Equal(SagaProcessOutcome.Processed, await UseAsync(options, saga =>
            saga.RecordResultAsync(result, CancellationToken.None)));
        await using var db = new OrderingDbContext(options);
        Assert.Equal(CheckoutState.Failed,
            (await db.OrderIntents.SingleAsync(x => x.Id == fact.OrderId)).State);
        var receipt = Assert.Single(await db.ResultInbox.ToArrayAsync());
        Assert.Equal("HoldExpired", receipt.ReasonCode);
        Assert.Equal(nameof(InventorySaleRejectedV1), receipt.MessageType);
    }

    [Fact]
    public async Task Conflicting_terminal_outcomes_cannot_overwrite_a_committed_sale()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitAsync(postgres);
        var fact = await SeedAsync(options);
        await ScheduleAsync(options, fact);
        var committed = new InventorySaleCommittedV1(Guid.NewGuid(), fact.OrderId, fact.HoldId,
            DateTimeOffset.UtcNow);
        await UseAsync(options, saga => saga.RecordResultAsync(committed, CancellationToken.None));
        var rejected = new InventorySaleRejectedV1(Guid.NewGuid(), fact.OrderId, fact.HoldId,
            "HoldExpired", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidDataException>(() => UseAsync(options, saga =>
            saga.RecordResultAsync(rejected, CancellationToken.None)));
        await using var db = new OrderingDbContext(options);
        Assert.Equal(CheckoutState.Completed,
            (await db.OrderIntents.SingleAsync(x => x.Id == fact.OrderId)).State);
        Assert.Equal(committed.MessageId, Assert.Single(await db.ResultInbox.ToArrayAsync()).MessageId);
    }

    [Fact]
    public async Task Forged_checkout_fact_cannot_schedule_sale()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitAsync(postgres);
        var fact = await SeedAsync(options);
        var forged = fact with { MessageId = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidDataException>(() => ScheduleAsync(options, forged));
        await using var db = new OrderingDbContext(options);
        Assert.Single(await db.OutboxMessages.ToArrayAsync());
    }

    [Fact]
    public async Task Unsolicited_result_does_not_complete_pending_order()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitAsync(postgres);
        var fact = await SeedAsync(options);
        var result = new InventorySaleCommittedV1(Guid.NewGuid(), fact.OrderId, fact.HoldId,
            DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidDataException>(() => UseAsync(options, saga =>
            saga.RecordResultAsync(result, CancellationToken.None)));
        await using var db = new OrderingDbContext(options);
        Assert.Equal(CheckoutState.Pending,
            (await db.OrderIntents.SingleAsync(x => x.Id == fact.OrderId)).State);
        Assert.Empty(await db.ResultInbox.ToArrayAsync());
    }

    [Fact]
    public async Task Inbox_insert_failure_rolls_back_order_state_transition()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitAsync(postgres);
        var fact = await SeedAsync(options);
        await ScheduleAsync(options, fact);
        await using (var setup = new OrderingDbContext(options))
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_saga_inbox() RETURNS trigger AS $$
                BEGIN
                    RAISE EXCEPTION 'injected saga inbox failure';
                END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_saga_inbox_insert BEFORE INSERT ON ordering_result_inbox
                    FOR EACH ROW EXECUTE FUNCTION fail_saga_inbox();
                """);
        var result = new InventorySaleCommittedV1(Guid.NewGuid(), fact.OrderId, fact.HoldId,
            DateTimeOffset.UtcNow);
        await Assert.ThrowsAnyAsync<Exception>(() => UseAsync(options, saga =>
            saga.RecordResultAsync(result, CancellationToken.None)));
        await using (var db = new OrderingDbContext(options))
        {
            Assert.Equal(CheckoutState.Pending,
                (await db.OrderIntents.SingleAsync(x => x.Id == fact.OrderId)).State);
            Assert.Empty(await db.ResultInbox.ToArrayAsync());
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_saga_inbox_insert ON ordering_result_inbox;");
            await db.Database.ExecuteSqlRawAsync("DROP FUNCTION fail_saga_inbox();");
        }
        Assert.Equal(SagaProcessOutcome.Processed, await UseAsync(options, saga =>
            saga.RecordResultAsync(result, CancellationToken.None)));
    }

    [Fact]
    public async Task Two_simultaneous_saga_consumers_schedule_only_one_command()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await InitAsync(postgres);
        var fact = await SeedAsync(options);
        var work = Enumerable.Range(0, 8).Select(_ => ScheduleAsync(options, fact));
        var outcomes = await Task.WhenAll(work);
        Assert.Single(outcomes, x => x == SagaProcessOutcome.Processed);
        Assert.Equal(7, outcomes.Count(x => x == SagaProcessOutcome.Duplicate));
        await using var db = new OrderingDbContext(options);
        Assert.Equal(2, await db.OutboxMessages.CountAsync());
    }
}

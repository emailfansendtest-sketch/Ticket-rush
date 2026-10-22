using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.BuildingBlocks.Contracts.Payment;
using TicketRush.Ordering.Persistence;
using Xunit;

namespace TicketRush.IntegrationTests;

/// <summary>PostgreSQL-backed Payment gate tests; no simulated broker mocks needed.</summary>
public sealed class OrderingPaymentSagaTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<DbContextOptions<OrderingDbContext>> CreateAsync(PostgreSqlContainer pg)
    {
        await pg.StartAsync();
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(pg.GetConnectionString()).Options;
        await using var db = new OrderingDbContext(options);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        return options;
    }

    private static async Task<CheckoutRequestedV1> AcceptAsync(
        DbContextOptions<OrderingDbContext> options, bool decline = false)
    {
        await using var db = new OrderingDbContext(options);
        var accepted = await new CheckoutIntakeService(db, TimeProvider.System)
            .AcceptAsync(Guid.NewGuid(), Guid.NewGuid(), "payment-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None, paymentRequired: true, simulatePaymentDecline: decline);
        Assert.Equal(CheckoutAdmissionKind.Accepted, accepted.Kind);
        var fact = Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync());
        return JsonSerializer.Deserialize<CheckoutRequestedV1>(fact.PayloadJson, JsonOptions)!;
    }

    private static async Task<T> SagaAsync<T>(DbContextOptions<OrderingDbContext> options,
        Func<OrderingSagaProcessor, Task<T>> action)
    {
        await using var db = new OrderingDbContext(options);
        return await action(new OrderingSagaProcessor(db, TimeProvider.System));
    }

    private static async Task<PaymentAuthorizationRequestedV1> PaymentCommandAsync(
        DbContextOptions<OrderingDbContext> options, CheckoutRequestedV1 fact)
    {
        await SagaAsync(options, saga => saga.ScheduleInventoryAsync(fact, CancellationToken.None));
        await using var db = new OrderingDbContext(options);
        var msg = await db.OutboxMessages.SingleAsync(x => x.OrderId == fact.OrderId &&
            x.MessageType == nameof(PaymentAuthorizationRequestedV1));
        return JsonSerializer.Deserialize<PaymentAuthorizationRequestedV1>(msg.PayloadJson, JsonOptions)!;
    }

    [Fact]
    public async Task Payment_required_checkout_schedules_payment_before_inventory()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await CreateAsync(pg);
        var fact = await AcceptAsync(options, decline: true);
        var cmd = await PaymentCommandAsync(options, fact);
        Assert.Equal(fact.OrderId, cmd.OrderId);
        Assert.Equal(fact.BuyerId, cmd.BuyerId);
        Assert.True(cmd.SimulateDecline);
        Assert.Equal(OrderingSagaProcessor.SandboxAmountMinor, cmd.AmountMinor);
        Assert.Equal(SagaProcessOutcome.Duplicate, await SagaAsync(options,
            saga => saga.ScheduleInventoryAsync(fact, CancellationToken.None)));
        await using var db = new OrderingDbContext(options);
        Assert.False(await db.OutboxMessages.AnyAsync(x => x.MessageType == nameof(InventorySaleRequestedV1)));
    }

    [Fact]
    public async Task Payment_decline_marks_order_failed_without_selling_or_scheduling_inventory()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await CreateAsync(pg);
        var fact = await AcceptAsync(options, true);
        _ = await PaymentCommandAsync(options, fact);
        var result = new PaymentDeclinedV1(Guid.NewGuid(), fact.OrderId, "SimulatedDecline", DateTimeOffset.UtcNow);
        Assert.Equal(SagaProcessOutcome.Processed, await SagaAsync(options,
            saga => saga.RecordPaymentResultAsync(result, CancellationToken.None)));
        Assert.Equal(SagaProcessOutcome.Duplicate, await SagaAsync(options,
            saga => saga.RecordPaymentResultAsync(result, CancellationToken.None)));
        await using var db = new OrderingDbContext(options);
        Assert.Equal(CheckoutState.Failed, (await db.OrderIntents.SingleAsync()).State);
        Assert.False(await db.OutboxMessages.AnyAsync(x => x.MessageType == nameof(InventorySaleRequestedV1)));
        Assert.Equal("SimulatedDecline", (await new CheckoutStatusReader(db)
            .GetAsync(fact.OrderId, fact.BuyerId, CancellationToken.None))?.FailureReasonCode);
    }

    [Fact]
    public async Task Authorized_payment_schedules_one_inventory_command_then_terminal_sale()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await CreateAsync(pg);
        var fact = await AcceptAsync(options);
        _ = await PaymentCommandAsync(options, fact);
        var auth = new PaymentAuthorizedV1(Guid.NewGuid(), fact.OrderId,
            OrderingSagaProcessor.SandboxAmountMinor, OrderingSagaProcessor.SandboxCurrency, DateTimeOffset.UtcNow);
        Assert.Equal(SagaProcessOutcome.Processed, await SagaAsync(options,
            saga => saga.RecordPaymentResultAsync(auth, CancellationToken.None)));
        Assert.Equal(SagaProcessOutcome.Duplicate, await SagaAsync(options,
            saga => saga.RecordPaymentResultAsync(auth, CancellationToken.None)));
        await using (var db = new OrderingDbContext(options))
        {
            Assert.Equal(CheckoutState.Pending, (await db.OrderIntents.SingleAsync()).State);
            Assert.Single(await db.OutboxMessages.Where(x =>
                x.MessageType == nameof(InventorySaleRequestedV1)).ToArrayAsync());
        }
        await SagaAsync(options, saga => saga.RecordResultAsync(new InventorySaleCommittedV1(
            Guid.NewGuid(), fact.OrderId, fact.HoldId, DateTimeOffset.UtcNow), CancellationToken.None));
        await using var verify = new OrderingDbContext(options);
        Assert.Equal(CheckoutState.Completed, (await verify.OrderIntents.SingleAsync()).State);
    }

    [Fact]
    public async Task Unsolicited_or_wrong_amount_payment_event_cannot_authorize_inventory()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await CreateAsync(pg);
        var fact = await AcceptAsync(options);
        var eventValue = new PaymentAuthorizedV1(Guid.NewGuid(), fact.OrderId,
            OrderingSagaProcessor.SandboxAmountMinor, OrderingSagaProcessor.SandboxCurrency, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidDataException>(() => SagaAsync(options,
            saga => saga.RecordPaymentResultAsync(eventValue, CancellationToken.None)));
        _ = await PaymentCommandAsync(options, fact);
        await Assert.ThrowsAsync<InvalidDataException>(() => SagaAsync(options,
            saga => saga.RecordPaymentResultAsync(eventValue with { AmountMinor = 2 }, CancellationToken.None)));
        await using var db = new OrderingDbContext(options);
        Assert.Empty(await db.PaymentInbox.ToArrayAsync());
    }

    [Fact]
    public async Task Inventory_cannot_complete_payment_required_checkout_before_authorization()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await CreateAsync(pg);
        var fact = await AcceptAsync(options);
        _ = await PaymentCommandAsync(options, fact);
        var forged = new InventorySaleCommittedV1(Guid.NewGuid(), fact.OrderId, fact.HoldId, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<InvalidDataException>(() => SagaAsync(options,
            saga => saga.RecordResultAsync(forged, CancellationToken.None)));
        await using var db = new OrderingDbContext(options);
        Assert.Equal(CheckoutState.Pending, (await db.OrderIntents.SingleAsync()).State);
    }

    [Fact]
    public async Task Conflicting_payment_decisions_do_not_override_authorized_result()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await CreateAsync(pg);
        var fact = await AcceptAsync(options);
        _ = await PaymentCommandAsync(options, fact);
        var success = new PaymentAuthorizedV1(Guid.NewGuid(), fact.OrderId,
            OrderingSagaProcessor.SandboxAmountMinor, OrderingSagaProcessor.SandboxCurrency, DateTimeOffset.UtcNow);
        await SagaAsync(options, saga => saga.RecordPaymentResultAsync(success, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => SagaAsync(options,
            saga => saga.RecordPaymentResultAsync(new PaymentDeclinedV1(Guid.NewGuid(), fact.OrderId,
                "OtherResult", DateTimeOffset.UtcNow), CancellationToken.None)));
        await using var db = new OrderingDbContext(options);
        Assert.Single(await db.PaymentInbox.ToArrayAsync());
    }

    [Fact]
    public async Task Watchdog_rearms_original_payment_command_not_checkout_fact()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await CreateAsync(pg);
        var fact = await AcceptAsync(options);
        var command = await PaymentCommandAsync(options, fact);
        await using (var db = new OrderingDbContext(options))
        {
            var order = await db.OrderIntents.SingleAsync();
            order.RequestedAtUtc = order.RequestedAtUtc.AddMinutes(-10);
            var msg = await db.OutboxMessages.SingleAsync(x =>
                x.MessageType == nameof(PaymentAuthorizationRequestedV1));
            msg.PublishedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-3);
            await db.SaveChangesAsync();
        }
        await using (var db = new OrderingDbContext(options))
        {
            var recovered = await new SagaRecoveryDispatcher(db, TimeProvider.System,
                SagaRecoveryOptions.Default).RecoverNextAsync(CancellationToken.None);
            Assert.Equal(SagaRecoveryOutcome.PaymentRepublishScheduled, recovered);
        }
        await using var verify = new OrderingDbContext(options);
        var pending = await verify.OutboxMessages.SingleAsync(x =>
            x.MessageType == nameof(PaymentAuthorizationRequestedV1));
        Assert.Equal(command.MessageId, pending.MessageId);
        Assert.Null(pending.PublishedAtUtc);
    }

    [Fact]
    public async Task Payment_result_inbox_failure_rolls_back_inventory_command()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await CreateAsync(pg);
        var fact = await AcceptAsync(options);
        _ = await PaymentCommandAsync(options, fact);
        await using (var db = new OrderingDbContext(options))
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION fail_payment_inbox() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'injected Payment inbox failure'; END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER fail_payment_inbox_insert BEFORE INSERT ON ordering_payment_inbox
                    FOR EACH ROW EXECUTE FUNCTION fail_payment_inbox();
                """);
        await Assert.ThrowsAnyAsync<Exception>(() => SagaAsync(options,
            saga => saga.RecordPaymentResultAsync(new PaymentAuthorizedV1(Guid.NewGuid(), fact.OrderId,
                OrderingSagaProcessor.SandboxAmountMinor, OrderingSagaProcessor.SandboxCurrency,
                DateTimeOffset.UtcNow), CancellationToken.None)));
        await using var verify = new OrderingDbContext(options);
        Assert.Empty(await verify.PaymentInbox.ToArrayAsync());
        Assert.False(await verify.OutboxMessages.AnyAsync(x =>
            x.MessageType == nameof(InventorySaleRequestedV1)));
    }
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.BuildingBlocks.Contracts.Payment;
using TicketRush.BuildingBlocks.Contracts.Notification;
using TicketRush.Ordering.Persistence;
using Xunit;

namespace TicketRush.IntegrationTests;

/// <summary>
/// The original CheckoutRequested outbox event can be delivered again after
/// the saga is terminal. It must never restart Payment, Inventory, or Notification.
/// This complements OrderingOutboxDispatchTests.Broker_ack_followed_by_database_failure_may_redeliver_same_message.
/// </summary>
public sealed class OrderingCheckoutFactReplayTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<DbContextOptions<OrderingDbContext>> CreateAsync(PostgreSqlContainer pg)
    {
        await pg.StartAsync();
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(pg.GetConnectionString()).Options;
        await using var db = new OrderingDbContext(options);
        await db.Database.MigrateAsync();
        return options;
    }

    private static async Task<CheckoutRequestedV1> AcceptAsync(
        DbContextOptions<OrderingDbContext> options, bool decline)
    {
        await using var db = new OrderingDbContext(options);
        var admission = await new CheckoutIntakeService(db, TimeProvider.System)
            .AcceptAsync(Guid.NewGuid(), Guid.NewGuid(), "replay-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None, paymentRequired: true, simulatePaymentDecline: decline);
        Assert.Equal(CheckoutAdmissionKind.Accepted, admission.Kind);
        var fact = Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync());
        return JsonSerializer.Deserialize<CheckoutRequestedV1>(fact.PayloadJson, JsonOptions)!;
    }

    private static async Task<T> SagaAsync<T>(DbContextOptions<OrderingDbContext> options,
        Func<OrderingSagaProcessor, Task<T>> action)
    {
        await using var db = new OrderingDbContext(options);
        return await action(new OrderingSagaProcessor(db, TimeProvider.System));
    }

    [Fact]
    public async Task Replaying_original_checkout_fact_after_sale_does_not_create_second_business_commands()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await CreateAsync(pg);
        var fact = await AcceptAsync(options, decline: false);
        Assert.Equal(SagaProcessOutcome.Processed, await SagaAsync(options,
            saga => saga.ScheduleInventoryAsync(fact, CancellationToken.None)));
        var authorized = new PaymentAuthorizedV1(Guid.NewGuid(), fact.OrderId,
            OrderingSagaProcessor.SandboxAmountMinor, OrderingSagaProcessor.SandboxCurrency,
            DateTimeOffset.UtcNow);
        Assert.Equal(SagaProcessOutcome.Processed, await SagaAsync(options,
            saga => saga.RecordPaymentResultAsync(authorized, CancellationToken.None)));
        var committed = new InventorySaleCommittedV1(Guid.NewGuid(), fact.OrderId, fact.HoldId,
            DateTimeOffset.UtcNow);
        Assert.Equal(SagaProcessOutcome.Processed, await SagaAsync(options,
            saga => saga.RecordResultAsync(committed, CancellationToken.None)));

        // One or more deliveries of the SAME durable CheckoutRequested fact after completion.
        Assert.Equal(SagaProcessOutcome.Duplicate, await SagaAsync(options,
            saga => saga.ScheduleInventoryAsync(fact, CancellationToken.None)));
        Assert.Equal(SagaProcessOutcome.Duplicate, await SagaAsync(options,
            saga => saga.ScheduleInventoryAsync(fact, CancellationToken.None)));

        await using var verify = new OrderingDbContext(options);
        Assert.Equal(CheckoutState.Completed, (await verify.OrderIntents.SingleAsync()).State);
        var outbox = await verify.OutboxMessages.AsNoTracking().ToArrayAsync();
        Assert.Equal(4, outbox.Length);
        Assert.Single(outbox, x => x.MessageType == nameof(CheckoutRequestedV1) && x.MessageId == fact.MessageId);
        Assert.Single(outbox, x => x.MessageType == nameof(PaymentAuthorizationRequestedV1));
        Assert.Single(outbox, x => x.MessageType == nameof(InventorySaleRequestedV1));
        Assert.Single(outbox, x => x.MessageType == nameof(CheckoutNotificationRequestedV1));
        Assert.Single(await verify.PaymentInbox.ToArrayAsync());
        Assert.Single(await verify.ResultInbox.ToArrayAsync());
    }

    [Fact]
    public async Task Replaying_original_checkout_fact_after_payment_decline_creates_no_inventory_command()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await CreateAsync(pg);
        var fact = await AcceptAsync(options, decline: true);
        Assert.Equal(SagaProcessOutcome.Processed, await SagaAsync(options,
            saga => saga.ScheduleInventoryAsync(fact, CancellationToken.None)));
        var declined = new PaymentDeclinedV1(Guid.NewGuid(), fact.OrderId,
            "SimulatedDecline", DateTimeOffset.UtcNow);
        Assert.Equal(SagaProcessOutcome.Processed, await SagaAsync(options,
            saga => saga.RecordPaymentResultAsync(declined, CancellationToken.None)));
        Assert.Equal(SagaProcessOutcome.Duplicate, await SagaAsync(options,
            saga => saga.ScheduleInventoryAsync(fact, CancellationToken.None)));

        await using var verify = new OrderingDbContext(options);
        Assert.Equal(CheckoutState.Failed, (await verify.OrderIntents.SingleAsync()).State);
        var outbox = await verify.OutboxMessages.AsNoTracking().ToArrayAsync();
        Assert.Equal(3, outbox.Length);
        Assert.Single(outbox, x => x.MessageType == nameof(CheckoutRequestedV1));
        Assert.Single(outbox, x => x.MessageType == nameof(PaymentAuthorizationRequestedV1));
        Assert.Single(outbox, x => x.MessageType == nameof(CheckoutNotificationRequestedV1));
        Assert.DoesNotContain(outbox, x => x.MessageType == nameof(InventorySaleRequestedV1));
        Assert.Single(await verify.PaymentInbox.ToArrayAsync());
        Assert.Empty(await verify.ResultInbox.ToArrayAsync());
    }
}

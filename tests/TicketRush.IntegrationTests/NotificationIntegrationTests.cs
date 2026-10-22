using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.BuildingBlocks.Contracts.Notification;
using TicketRush.BuildingBlocks.Contracts.Payment;
using TicketRush.Notification.Persistence;
using TicketRush.Ordering.Persistence;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class NotificationIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<DbContextOptions<NotificationDbContext>> NotificationDbAsync(
        PostgreSqlContainer postgres)
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<NotificationDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new NotificationDbContext(options);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        return options;
    }

    private static async Task<DbContextOptions<OrderingDbContext>> OrderingDbAsync(
        PostgreSqlContainer postgres)
    {
        await postgres.StartAsync();
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(postgres.GetConnectionString()).Options;
        await using var db = new OrderingDbContext(options);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        return options;
    }

    private static CheckoutNotificationRequestedV1 Message(string outcome = "Completed") =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), outcome,
            outcome == "Failed" ? "HoldExpired" : null,
            new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Notification_receipt_is_durable_and_duplicate_is_idempotent()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await NotificationDbAsync(postgres);
        var message = Message();
        await using (var db = new NotificationDbContext(options))
        {
            var processor = new NotificationDeliveryProcessor(db, TimeProvider.System);
            Assert.Equal(NotificationProcessOutcome.Recorded,
                await processor.ProcessAsync(message, CancellationToken.None));
            Assert.Equal(NotificationProcessOutcome.Duplicate,
                await processor.ProcessAsync(message, CancellationToken.None));
        }
        await using var verify = new NotificationDbContext(options);
        var receipt = Assert.Single(await verify.Receipts.AsNoTracking().ToArrayAsync());
        Assert.Equal(message.MessageId, receipt.MessageId);
        Assert.Equal(message.OrderId, receipt.OrderId);
        Assert.Equal("Completed", receipt.Outcome);
    }

    [Fact]
    public async Task Conflicting_replays_cannot_overwrite_a_durable_notification()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await NotificationDbAsync(postgres);
        var message = Message("Failed");
        await using var db = new NotificationDbContext(options);
        var processor = new NotificationDeliveryProcessor(db, TimeProvider.System);
        await processor.ProcessAsync(message, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(
            message with { MessageId = Guid.NewGuid() }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(
            message with { ReasonCode = "Different" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(
            message with { OrderId = Guid.NewGuid() }, CancellationToken.None));
        Assert.Single(await db.Receipts.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Invalid_outcomes_and_identifiers_are_rejected_before_writing()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await NotificationDbAsync(postgres);
        await using var db = new NotificationDbContext(options);
        var processor = new NotificationDeliveryProcessor(db, TimeProvider.System);
        var message = Message();
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(
            message with { Outcome = "Pending" }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(
            message with { MessageId = Guid.Empty }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(
            message with { ReasonCode = "Unexpected" }, CancellationToken.None));
        Assert.Empty(await db.Receipts.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Competing_consumers_persist_only_one_receipt()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await NotificationDbAsync(postgres);
        var command = Message();
        var work = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = new NotificationDbContext(options);
            return await new NotificationDeliveryProcessor(db, TimeProvider.System)
                .ProcessAsync(command, CancellationToken.None);
        });
        var results = await Task.WhenAll(work);
        Assert.Single(results, x => x == NotificationProcessOutcome.Recorded);
        Assert.Equal(7, results.Count(x => x == NotificationProcessOutcome.Duplicate));
        await using var verify = new NotificationDbContext(options);
        Assert.Single(await verify.Receipts.AsNoTracking().ToArrayAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inventory_terminal_outcome_creates_one_matching_notification(bool committed)
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await OrderingDbAsync(postgres);
        await using var db = new OrderingDbContext(options);
        var admission = await new CheckoutIntakeService(db, TimeProvider.System)
            .AcceptAsync(Guid.NewGuid(), Guid.NewGuid(), "notification-" + Guid.NewGuid().ToString("N"),
                CancellationToken.None);
        Assert.Equal(CheckoutAdmissionKind.Accepted, admission.Kind);
        var factRow = await db.OutboxMessages.SingleAsync(x => x.OrderId == admission.OrderId &&
            x.MessageType == nameof(CheckoutRequestedV1));
        var fact = JsonSerializer.Deserialize<CheckoutRequestedV1>(factRow.PayloadJson, JsonOptions);
        Assert.NotNull(fact);
        var saga = new OrderingSagaProcessor(db, TimeProvider.System);
        await saga.ScheduleInventoryAsync(fact, CancellationToken.None);
        Assert.False(await db.OutboxMessages.AsNoTracking().AnyAsync(x =>
            x.MessageType == nameof(CheckoutNotificationRequestedV1)));
        var outcome = committed
            ? await saga.RecordResultAsync(new InventorySaleCommittedV1(
                Guid.NewGuid(), fact.OrderId, fact.HoldId, DateTimeOffset.UtcNow), CancellationToken.None)
            : await saga.RecordResultAsync(new InventorySaleRejectedV1(
                Guid.NewGuid(), fact.OrderId, fact.HoldId, "HoldExpired", DateTimeOffset.UtcNow),
                CancellationToken.None);
        Assert.Equal(SagaProcessOutcome.Processed, outcome);
        var notificationRow = await db.OutboxMessages.AsNoTracking().SingleAsync(x =>
            x.OrderId == fact.OrderId && x.MessageType == nameof(CheckoutNotificationRequestedV1));
        var notification = JsonSerializer.Deserialize<CheckoutNotificationRequestedV1>(
            notificationRow.PayloadJson, JsonOptions);
        Assert.NotNull(notification);
        Assert.Equal(notificationRow.MessageId, notification.MessageId);
        Assert.Equal(fact.BuyerId, notification.BuyerId);
        Assert.Equal(committed ? "Completed" : "Failed", notification.Outcome);
        Assert.Equal(committed ? null : "HoldExpired", notification.ReasonCode);
    }

    [Fact]
    public async Task Payment_decline_records_a_failed_checkout_notification_without_inventory_command()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await OrderingDbAsync(postgres);
        await using var db = new OrderingDbContext(options);
        var admission = await new CheckoutIntakeService(db, TimeProvider.System)
            .AcceptAsync(Guid.NewGuid(), Guid.NewGuid(), "notify-decline", CancellationToken.None,
                paymentRequired: true, simulatePaymentDecline: true);
        Assert.Equal(CheckoutAdmissionKind.Accepted, admission.Kind);
        var factRow = await db.OutboxMessages.SingleAsync(x => x.OrderId == admission.OrderId &&
            x.MessageType == nameof(CheckoutRequestedV1));
        var fact = JsonSerializer.Deserialize<CheckoutRequestedV1>(factRow.PayloadJson, JsonOptions);
        Assert.NotNull(fact);
        var saga = new OrderingSagaProcessor(db, TimeProvider.System);
        await saga.ScheduleInventoryAsync(fact, CancellationToken.None);
        var decline = new PaymentDeclinedV1(Guid.NewGuid(), fact.OrderId, "SimulatedDecline",
            DateTimeOffset.UtcNow);
        Assert.Equal(SagaProcessOutcome.Processed,
            await saga.RecordPaymentResultAsync(decline, CancellationToken.None));
        Assert.Equal(SagaProcessOutcome.Duplicate,
            await saga.RecordPaymentResultAsync(decline, CancellationToken.None));
        Assert.False(await db.OutboxMessages.AsNoTracking().AnyAsync(x =>
            x.OrderId == fact.OrderId && x.MessageType == nameof(InventorySaleRequestedV1)));
        var notificationRow = await db.OutboxMessages.AsNoTracking().SingleAsync(x =>
            x.OrderId == fact.OrderId && x.MessageType == nameof(CheckoutNotificationRequestedV1));
        var notification = JsonSerializer.Deserialize<CheckoutNotificationRequestedV1>(
            notificationRow.PayloadJson, JsonOptions);
        Assert.NotNull(notification);
        Assert.Equal("Failed", notification.Outcome);
        Assert.Equal("SimulatedDecline", notification.ReasonCode);
    }

    [Fact]
    public async Task Failed_notification_receipt_insert_does_not_leave_a_partial_simulated_delivery()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await NotificationDbAsync(postgres);
        await using (var db = new NotificationDbContext(options))
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE FUNCTION reject_notification_insert() RETURNS trigger AS $$
                BEGIN RAISE EXCEPTION 'forced notification receipt failure'; END;
                $$ LANGUAGE plpgsql;
                CREATE TRIGGER reject_notification_insert BEFORE INSERT ON notification_receipts
                FOR EACH ROW EXECUTE FUNCTION reject_notification_insert();
                """);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                new NotificationDeliveryProcessor(db, TimeProvider.System)
                    .ProcessAsync(Message(), CancellationToken.None));
        }
        await using var verify = new NotificationDbContext(options);
        Assert.Empty(await verify.Receipts.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Failed_notification_outbox_insert_rolls_back_the_terminal_order_transition()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await OrderingDbAsync(postgres);
        await using var db = new OrderingDbContext(options);
        var admission = await new CheckoutIntakeService(db, TimeProvider.System)
            .AcceptAsync(Guid.NewGuid(), Guid.NewGuid(), "rollback-notification", CancellationToken.None);
        var factRow = await db.OutboxMessages.SingleAsync(x => x.OrderId == admission.OrderId &&
            x.MessageType == nameof(CheckoutRequestedV1));
        var fact = JsonSerializer.Deserialize<CheckoutRequestedV1>(factRow.PayloadJson, JsonOptions);
        Assert.NotNull(fact);
        var saga = new OrderingSagaProcessor(db, TimeProvider.System);
        await saga.ScheduleInventoryAsync(fact, CancellationToken.None);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION reject_notification_outbox() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'forced terminal notification failure'; END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER reject_notification_outbox BEFORE INSERT ON outbox_messages
            FOR EACH ROW WHEN (NEW.message_type = 'CheckoutNotificationRequestedV1')
            EXECUTE FUNCTION reject_notification_outbox();
            """);
        await Assert.ThrowsAnyAsync<Exception>(() => saga.RecordResultAsync(
            new InventorySaleCommittedV1(Guid.NewGuid(), fact.OrderId, fact.HoldId,
                DateTimeOffset.UtcNow), CancellationToken.None));
        await using var verify = new OrderingDbContext(options);
        Assert.Equal(CheckoutState.Pending,
            (await verify.OrderIntents.SingleAsync(x => x.Id == fact.OrderId)).State);
        Assert.Empty(await verify.ResultInbox.AsNoTracking().ToArrayAsync());
        Assert.False(await verify.OutboxMessages.AsNoTracking().AnyAsync(x =>
            x.MessageType == nameof(CheckoutNotificationRequestedV1)));
    }
}

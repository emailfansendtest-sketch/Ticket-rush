using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.BuildingBlocks.Contracts.Payment;
using TicketRush.Ordering.Persistence;
using Xunit;

namespace TicketRush.IntegrationTests;

public sealed class OrderingCompensationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static async Task<DbContextOptions<OrderingDbContext>> OpenAsync(PostgreSqlContainer pg)
    {
        await pg.StartAsync();
        var options = new DbContextOptionsBuilder<OrderingDbContext>()
            .UseNpgsql(pg.GetConnectionString()).Options;
        await using var db = new OrderingDbContext(options);
        await db.Database.MigrateAsync();
        Assert.False(db.Database.HasPendingModelChanges());
        return options;
    }
    private static async Task<(Guid orderId, Guid holdId, Guid buyerId, Guid authCommandId)>
        AuthorizedAsync(DbContextOptions<OrderingDbContext> options)
    {
        await using var db = new OrderingDbContext(options);
        var intake = await new CheckoutIntakeService(db, TimeProvider.System).AcceptAsync(
            Guid.NewGuid(), Guid.NewGuid(), "void-" + Guid.NewGuid().ToString("N"),
            CancellationToken.None, paymentRequired: true);
        var factRow = await db.OutboxMessages.SingleAsync(x =>
            x.MessageType == nameof(CheckoutRequestedV1));
        var fact = JsonSerializer.Deserialize<CheckoutRequestedV1>(factRow.PayloadJson, JsonOptions);
        Assert.NotNull(fact);
        var saga = new OrderingSagaProcessor(db, TimeProvider.System);
        await saga.ScheduleInventoryAsync(fact, CancellationToken.None);
        var auth = await db.OutboxMessages.SingleAsync(x =>
            x.OrderId == intake.OrderId && x.MessageType == nameof(PaymentAuthorizationRequestedV1));
        await saga.RecordPaymentResultAsync(new PaymentAuthorizedV1(
            Guid.NewGuid(), fact.OrderId, OrderingSagaProcessor.SandboxAmountMinor,
            OrderingSagaProcessor.SandboxCurrency, DateTimeOffset.UtcNow), CancellationToken.None);
        return (fact.OrderId, fact.HoldId, fact.BuyerId, auth.MessageId);
    }

    [Fact]
    public async Task Inventory_rejection_schedules_one_void_and_failed_status_reports_pending_compensation()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await OpenAsync(pg);
        var (orderId, holdId, buyerId, authId) = await AuthorizedAsync(options);
        await using var db = new OrderingDbContext(options);
        var saga = new OrderingSagaProcessor(db, TimeProvider.System);
        var rejected = new InventorySaleRejectedV1(Guid.NewGuid(), orderId, holdId,
            "HoldExpired", DateTimeOffset.UtcNow);
        Assert.Equal(SagaProcessOutcome.Processed,
            await saga.RecordResultAsync(rejected, CancellationToken.None));
        Assert.Equal(SagaProcessOutcome.Duplicate,
            await saga.RecordResultAsync(rejected, CancellationToken.None));
        var outbox = await db.OutboxMessages.SingleAsync(x =>
            x.OrderId == orderId && x.MessageType == nameof(PaymentVoidRequestedV1));
        var payload = JsonSerializer.Deserialize<PaymentVoidRequestedV1>(outbox.PayloadJson, JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(authId, payload.AuthorizationCommandId);
        var status = await new CheckoutStatusReader(db).GetAsync(orderId, buyerId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal("Failed", status.State);
        Assert.Equal("Pending", status.CompensationStatus);
    }

    [Fact]
    public async Task Voided_event_completes_compensation_and_duplicate_is_idempotent()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await OpenAsync(pg);
        var (orderId, holdId, buyerId, authId) = await AuthorizedAsync(options);
        await using var db = new OrderingDbContext(options);
        var saga = new OrderingSagaProcessor(db, TimeProvider.System);
        await saga.RecordResultAsync(new InventorySaleRejectedV1(Guid.NewGuid(), orderId,
            holdId, "HoldExpired", DateTimeOffset.UtcNow), CancellationToken.None);
        var voidCmd = await db.OutboxMessages.SingleAsync(x =>
            x.OrderId == orderId && x.MessageType == nameof(PaymentVoidRequestedV1));
        var result = new PaymentVoidedV1(Guid.NewGuid(), orderId, voidCmd.MessageId,
            authId, DateTimeOffset.UtcNow);
        Assert.Equal(SagaProcessOutcome.Processed,
            await saga.RecordVoidResultAsync(result, CancellationToken.None));
        Assert.Equal(SagaProcessOutcome.Duplicate,
            await saga.RecordVoidResultAsync(result, CancellationToken.None));
        var status = await new CheckoutStatusReader(db).GetAsync(orderId, buyerId, CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal("Voided", status.CompensationStatus);
        Assert.Equal("Failed", status.State);
        Assert.Null((await db.OrderIntents.SingleAsync()).NextRecoveryAtUtc);
        Assert.Single(await db.VoidInbox.ToArrayAsync());
    }

    [Fact]
    public async Task Wrong_void_command_is_rejected_without_recording_completion()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await OpenAsync(pg);
        var (orderId, holdId, _, authId) = await AuthorizedAsync(options);
        await using var db = new OrderingDbContext(options);
        var saga = new OrderingSagaProcessor(db, TimeProvider.System);
        await saga.RecordResultAsync(new InventorySaleRejectedV1(Guid.NewGuid(), orderId,
            holdId, "HoldExpired", DateTimeOffset.UtcNow), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => saga.RecordVoidResultAsync(
            new PaymentVoidedV1(Guid.NewGuid(), orderId, Guid.NewGuid(), authId,
                DateTimeOffset.UtcNow), CancellationToken.None));
        Assert.Empty(await db.VoidInbox.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Recovery_rearms_original_void_command_without_changing_failed_state()
    {
        await using var pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
        var options = await OpenAsync(pg);
        var (orderId, holdId, _, _) = await AuthorizedAsync(options);
        await using (var db = new OrderingDbContext(options))
        {
            var saga = new OrderingSagaProcessor(db, TimeProvider.System);
            await saga.RecordResultAsync(new InventorySaleRejectedV1(Guid.NewGuid(), orderId,
                holdId, "HoldExpired", DateTimeOffset.UtcNow), CancellationToken.None);
            var order = await db.OrderIntents.SingleAsync();
            var cmd = await db.OutboxMessages.SingleAsync(x =>
                x.MessageType == nameof(PaymentVoidRequestedV1));
            var stamp = DateTimeOffset.UtcNow;
            cmd.PublishedAtUtc = stamp.AddTicks(-(stamp.Ticks % 10));
            order.NextRecoveryAtUtc = stamp.AddMinutes(-1).AddTicks(-(stamp.Ticks % 10));
            await db.SaveChangesAsync();
        }
        await using (var db = new OrderingDbContext(options))
        {
            var recovery = new PaymentVoidRecoveryDispatcher(db, TimeProvider.System,
                SagaRecoveryOptions.Default);
            Assert.Equal(VoidRecoveryOutcome.RepublishScheduled,
                await recovery.RecoverNextAsync(CancellationToken.None));
        }
        await using var verify = new OrderingDbContext(options);
        Assert.Equal(CheckoutState.Failed, (await verify.OrderIntents.SingleAsync()).State);
        Assert.Null((await verify.OutboxMessages.SingleAsync(x =>
            x.MessageType == nameof(PaymentVoidRequestedV1))).PublishedAtUtc);
        Assert.Equal(1, (await verify.OrderIntents.SingleAsync()).RecoveryAttempts);
    }
}

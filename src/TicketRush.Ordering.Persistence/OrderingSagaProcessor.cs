using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.BuildingBlocks.Contracts.Payment;
using TicketRush.BuildingBlocks.Contracts.Notification;

namespace TicketRush.Ordering.Persistence;

public enum SagaProcessOutcome { Processed, Duplicate }

/// <summary>
/// Two local Ordering transactions: a checkout fact schedules an Inventory
/// command via the Ordering outbox; an Inventory result durably terminates the
/// Ordering intent via an inbox. No cross-database transaction is attempted.
/// </summary>
public sealed class OrderingSagaProcessor(OrderingDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Explicit sandbox parameters. Pricing is NOT calculated from TicketRush catalog data yet.
    public const long SandboxAmountMinor = 1000;
    public const string SandboxCurrency = "USD";

    public async Task<SagaProcessOutcome> ScheduleInventoryAsync(
        CheckoutRequestedV1 message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.MessageId == Guid.Empty || message.OrderId == Guid.Empty ||
            message.HoldId == Guid.Empty || message.BuyerId == Guid.Empty)
            throw new InvalidDataException("Checkout fact is missing required identifiers.");

        // The input must be the exact fact accepted in our own durable outbox.
        var fact = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(
            x => x.MessageId == message.MessageId && x.OrderId == message.OrderId &&
                 x.MessageType == nameof(CheckoutRequestedV1), ct);
        if (fact is null)
            throw new InvalidDataException("Unrecognized CheckoutRequestedV1 fact.");
        var original = JsonSerializer.Deserialize<CheckoutRequestedV1>(fact.PayloadJson, JsonOptions);
        if (original != message)
            throw new InvalidDataException("Checkout event does not match durable Ordering fact.");

        var order = await db.OrderIntents.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == message.OrderId, ct);
        if (order is null || order.HoldId != message.HoldId || order.BuyerId != message.BuyerId ||
            order.RequestedAtUtc != message.RequestedAtUtc)
            throw new InvalidDataException("Checkout fact does not match its OrderIntent.");

        var existing = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(
            x => x.OrderId == message.OrderId && x.MessageType == nameof(InventorySaleRequestedV1), ct);
        if (existing is not null)
        {
            ValidateCommand(existing, message);
            return SagaProcessOutcome.Duplicate;
        }
        if (order.PaymentRequired)
        {
            var payment = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(
                x => x.OrderId == message.OrderId &&
                     x.MessageType == nameof(PaymentAuthorizationRequestedV1), ct);
            if (payment is not null)
            {
                ValidatePaymentCommand(payment, order);
                return SagaProcessOutcome.Duplicate;
            }
        }
        if (order.State != CheckoutState.Pending)
            throw new InvalidDataException("Terminal order cannot schedule a new command.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var now = Truncate(clock.GetUtcNow());
            var commandId = Guid.CreateVersion7();
            var type = order.PaymentRequired
                ? nameof(PaymentAuthorizationRequestedV1)
                : nameof(InventorySaleRequestedV1);
            var payload = order.PaymentRequired
                ? JsonSerializer.Serialize(new PaymentAuthorizationRequestedV1(
                    commandId, message.OrderId, message.BuyerId,
                    SandboxAmountMinor, SandboxCurrency, order.SimulatePaymentDecline,
                    message.RequestedAtUtc), JsonOptions)
                : JsonSerializer.Serialize(new InventorySaleRequestedV1(commandId,
                    message.OrderId, message.HoldId, message.BuyerId,
                    message.RequestedAtUtc), JsonOptions);
            db.OutboxMessages.Add(new OutboxMessage
            {
                MessageId = commandId, OrderId = message.OrderId,
                MessageType = type, PayloadJson = payload, CreatedAtUtc = now
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SagaProcessOutcome.Processed;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var expectedType = order.PaymentRequired
                ? nameof(PaymentAuthorizationRequestedV1)
                : nameof(InventorySaleRequestedV1);
            existing = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(
                x => x.OrderId == message.OrderId && x.MessageType == expectedType, ct);
            if (existing is null) throw;
            if (order.PaymentRequired) ValidatePaymentCommand(existing, order);
            else ValidateCommand(existing, message);
            return SagaProcessOutcome.Duplicate;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public Task<SagaProcessOutcome> RecordPaymentResultAsync(
        PaymentAuthorizedV1 result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.AmountMinor != SandboxAmountMinor || result.Currency != SandboxCurrency)
            throw new InvalidDataException("Payment authorized unexpected amount/currency.");
        return RecordPaymentCoreAsync(result.MessageId, result.OrderId,
            nameof(PaymentAuthorizedV1), null, ct);
    }

    public Task<SagaProcessOutcome> RecordPaymentResultAsync(
        PaymentDeclinedV1 result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(result.ReasonCode) || result.ReasonCode.Length > 120)
            throw new InvalidDataException("Payment decline reason is missing or invalid.");
        return RecordPaymentCoreAsync(result.MessageId, result.OrderId,
            nameof(PaymentDeclinedV1), result.ReasonCode, ct);
    }

    private async Task<SagaProcessOutcome> RecordPaymentCoreAsync(
        Guid messageId, Guid orderId, string type, string? reason, CancellationToken ct)
    {
        if (messageId == Guid.Empty || orderId == Guid.Empty)
            throw new InvalidDataException("Payment event is missing identifiers.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Serialize Payment events, Inventory outcomes and watchdog changes
            // on the same OrderIntent row.
            var locked = await db.OrderIntents.FromSqlInterpolated($"""
                SELECT id, hold_id, buyer_id, idempotency_key, state, requested_at_utc,
                       payment_required, simulate_payment_decline,
                       recovery_attempts, next_recovery_at_utc, recovery_needs_attention
                FROM order_intents WHERE id = {orderId} FOR UPDATE
                """).ToListAsync(ct);
            var order = locked.SingleOrDefault()
                ?? throw new InvalidDataException("Payment result for unknown checkout.");
            if (!order.PaymentRequired)
                throw new InvalidDataException("Checkout does not require Payment.");
            var paymentCommand = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(
                x => x.OrderId == orderId &&
                     x.MessageType == nameof(PaymentAuthorizationRequestedV1), ct)
                ?? throw new InvalidDataException("Unsolicited Payment result.");
            ValidatePaymentCommand(paymentCommand, order);
            var receipt = await db.PaymentInbox.SingleOrDefaultAsync(
                x => x.OrderId == orderId, ct);
            if (receipt is not null)
            {
                if (receipt.MessageId != messageId || receipt.MessageType != type ||
                    receipt.ReasonCode != reason)
                    throw new InvalidDataException("Conflicting Payment results.");
                return SagaProcessOutcome.Duplicate;
            }
            if (order.State != CheckoutState.Pending)
                throw new InvalidDataException("Payment result after terminal checkout.");

            var now = Truncate(clock.GetUtcNow());
            db.PaymentInbox.Add(new OrderingPaymentInboxMessage
            {
                MessageId = messageId, OrderId = orderId, MessageType = type,
                ReasonCode = reason, ProcessedAtUtc = now
            });
            if (type == nameof(PaymentDeclinedV1))
            {
                // No Inventory sale has been requested, so no seat compensation is needed.
                order.State = CheckoutState.Failed;
                order.NextRecoveryAtUtc = null;
                order.RecoveryNeedsAttention = false;
                AddTerminalNotification(order, nameof(CheckoutState.Failed), reason, now);
            }
            else
            {
                var inventoryCommandId = Guid.CreateVersion7();
                var command = new InventorySaleRequestedV1(inventoryCommandId,
                    order.Id, order.HoldId, order.BuyerId, order.RequestedAtUtc);
                db.OutboxMessages.Add(new OutboxMessage
                {
                    MessageId = inventoryCommandId, OrderId = order.Id,
                    MessageType = nameof(InventorySaleRequestedV1),
                    PayloadJson = JsonSerializer.Serialize(command, JsonOptions),
                    CreatedAtUtc = now
                });
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SagaProcessOutcome.Processed;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public Task<SagaProcessOutcome> RecordResultAsync(
        InventorySaleCommittedV1 result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        return RecordResultCoreAsync(result.MessageId, result.OrderId, result.HoldId,
            nameof(InventorySaleCommittedV1), null, CheckoutState.Completed, ct);
    }

    public Task<SagaProcessOutcome> RecordResultAsync(
        InventorySaleRejectedV1 result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(result.ReasonCode) || result.ReasonCode.Length > 120)
            throw new InvalidDataException("Missing or invalid Inventory rejection reason.");
        return RecordResultCoreAsync(result.MessageId, result.OrderId, result.HoldId,
            nameof(InventorySaleRejectedV1), result.ReasonCode, CheckoutState.Failed, ct);
    }

    private async Task<SagaProcessOutcome> RecordResultCoreAsync(
        Guid messageId, Guid orderId, Guid holdId, string messageType,
        string? reason, CheckoutState target, CancellationToken ct)
    {
        if (messageId == Guid.Empty || orderId == Guid.Empty || holdId == Guid.Empty)
            throw new InvalidDataException("Inventory result is missing required identifiers.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Serialize outcomes for the same order. This also protects the
            // Pending -> terminal transition against competing distinct events.
            var locked = await db.OrderIntents.FromSqlInterpolated($"""
                SELECT id, hold_id, buyer_id, idempotency_key, state, requested_at_utc,
                       payment_required, simulate_payment_decline,
                       recovery_attempts, next_recovery_at_utc, recovery_needs_attention
                FROM order_intents
                WHERE id = {orderId}
                FOR UPDATE
                """).ToListAsync(ct);
            var order = locked.SingleOrDefault()
                ?? throw new InvalidDataException("Inventory result refers to an unknown Ordering intent.");
            if (order.HoldId != holdId)
                throw new InvalidDataException("Inventory result has the wrong hold ID.");
            // The result must correspond to a command that this saga emitted.
            if (!await db.OutboxMessages.AsNoTracking().AnyAsync(x =>
                    x.OrderId == orderId && x.MessageType == nameof(InventorySaleRequestedV1), ct))
                throw new InvalidDataException("Inventory result has no scheduled sale command.");
            if (order.PaymentRequired && !await db.PaymentInbox.AsNoTracking().AnyAsync(x =>
                    x.OrderId == orderId && x.MessageType == nameof(PaymentAuthorizedV1), ct))
                throw new InvalidDataException("Inventory outcome without Payment authorization.");

            var recorded = await db.ResultInbox.AsNoTracking().SingleOrDefaultAsync(
                x => x.OrderId == orderId, ct);
            if (recorded is not null)
            {
                if (recorded.MessageId != messageId || recorded.MessageType != messageType ||
                    recorded.HoldId != holdId || recorded.ReasonCode != reason || order.State != target)
                    throw new InvalidDataException("Conflicting terminal Inventory result for the order.");
                return SagaProcessOutcome.Duplicate;
            }
            if (order.State != CheckoutState.Pending)
                throw new InvalidDataException("Ordering intent is terminal without an inbox receipt.");

            order.State = target;
            // A late authoritative Inventory result resolves the Pending saga.
            // A rejected sale after authorization creates a separate durable
            // compensation command in the SAME transaction as the failed state.
            order.RecoveryNeedsAttention = false;
            order.NextRecoveryAtUtc = null;
            if (target == CheckoutState.Failed && order.PaymentRequired)
            {
                var auth = await db.OutboxMessages.AsNoTracking().SingleAsync(x =>
                    x.OrderId == orderId &&
                    x.MessageType == nameof(PaymentAuthorizationRequestedV1), ct);
                var voidId = Guid.CreateVersion7();
                var now = Truncate(clock.GetUtcNow());
                var voidRequest = new PaymentVoidRequestedV1(voidId, order.Id, order.BuyerId,
                    auth.MessageId, order.RequestedAtUtc);
                db.OutboxMessages.Add(new OutboxMessage
                {
                    MessageId = voidId, OrderId = order.Id,
                    MessageType = nameof(PaymentVoidRequestedV1),
                    PayloadJson = JsonSerializer.Serialize(voidRequest, JsonOptions),
                    CreatedAtUtc = now
                });
                // Recovery continues even though the business checkout is Failed.
                order.RecoveryAttempts = 0;
                order.NextRecoveryAtUtc = Truncate(now.AddMinutes(2));
            }
            AddTerminalNotification(order,
                target == CheckoutState.Completed ? nameof(CheckoutState.Completed) : nameof(CheckoutState.Failed),
                reason, Truncate(clock.GetUtcNow()));
            db.ResultInbox.Add(new OrderingResultInboxMessage
            {
                MessageId = messageId, OrderId = orderId, HoldId = holdId,
                MessageType = messageType, ReasonCode = reason,
                ProcessedAtUtc = Truncate(clock.GetUtcNow())
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SagaProcessOutcome.Processed;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<SagaProcessOutcome> RecordVoidResultAsync(
        PaymentVoidedV1 result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.MessageId == Guid.Empty || result.OrderId == Guid.Empty ||
            result.VoidCommandId == Guid.Empty || result.AuthorizationCommandId == Guid.Empty)
            throw new InvalidDataException("Void result has missing identifiers.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var locked = await db.OrderIntents.FromSqlInterpolated($"""
                SELECT id, hold_id, buyer_id, idempotency_key, state, requested_at_utc,
                       payment_required, simulate_payment_decline,
                       recovery_attempts, next_recovery_at_utc, recovery_needs_attention
                FROM order_intents WHERE id = {result.OrderId} FOR UPDATE
                """).ToListAsync(ct);
            var order = locked.SingleOrDefault()
                ?? throw new InvalidDataException("Void result for unknown checkout.");
            if (!order.PaymentRequired || order.State != CheckoutState.Failed ||
                !await db.ResultInbox.AsNoTracking().AnyAsync(x =>
                    x.OrderId == result.OrderId &&
                    x.MessageType == nameof(InventorySaleRejectedV1), ct) ||
                !await db.PaymentInbox.AsNoTracking().AnyAsync(x =>
                    x.OrderId == result.OrderId &&
                    x.MessageType == nameof(PaymentAuthorizedV1), ct))
                throw new InvalidDataException("Unsolicited or contradictory Payment void result.");
            var command = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrderId == result.OrderId &&
                x.MessageType == nameof(PaymentVoidRequestedV1), ct)
                ?? throw new InvalidDataException("Void result without scheduled command.");
            var expected = JsonSerializer.Deserialize<PaymentVoidRequestedV1>(
                command.PayloadJson, JsonOptions);
            if (expected is null || expected.MessageId != command.MessageId ||
                expected.OrderId != order.Id || expected.BuyerId != order.BuyerId ||
                result.VoidCommandId != expected.MessageId ||
                result.AuthorizationCommandId != expected.AuthorizationCommandId)
                throw new InvalidDataException("Void result does not match Ordering's command.");
            var previous = await db.VoidInbox.SingleOrDefaultAsync(x => x.OrderId == order.Id, ct);
            if (previous is not null)
            {
                if (previous.MessageId != result.MessageId ||
                    previous.VoidCommandId != result.VoidCommandId ||
                    previous.AuthorizationCommandId != result.AuthorizationCommandId)
                    throw new InvalidDataException("Conflicting Payment void results.");
                return SagaProcessOutcome.Duplicate;
            }
            db.VoidInbox.Add(new OrderingVoidInboxMessage
            {
                MessageId = result.MessageId, OrderId = order.Id,
                VoidCommandId = result.VoidCommandId,
                AuthorizationCommandId = result.AuthorizationCommandId,
                ProcessedAtUtc = Truncate(clock.GetUtcNow())
            });
            order.NextRecoveryAtUtc = null;
            order.RecoveryNeedsAttention = false;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SagaProcessOutcome.Processed;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    // Same transaction as the authoritative terminal saga transition.
    // Existing (OrderId, MessageType) outbox uniqueness guarantees one notification per checkout.
    private void AddTerminalNotification(OrderIntent order, string outcome, string? reason,
        DateTimeOffset now)
    {
        var notificationId = Guid.CreateVersion7();
        var notification = new CheckoutNotificationRequestedV1(
            notificationId, order.Id, order.BuyerId, outcome, reason, now);
        db.OutboxMessages.Add(new OutboxMessage
        {
            MessageId = notificationId,
            OrderId = order.Id,
            MessageType = nameof(CheckoutNotificationRequestedV1),
            PayloadJson = JsonSerializer.Serialize(notification, JsonOptions),
            CreatedAtUtc = now
        });
    }

    private static void ValidatePaymentCommand(OutboxMessage outbox, OrderIntent order)
    {
        var command = JsonSerializer.Deserialize<PaymentAuthorizationRequestedV1>(
            outbox.PayloadJson, JsonOptions);
        if (command is null || command.MessageId != outbox.MessageId ||
            command.OrderId != order.Id || command.BuyerId != order.BuyerId ||
            command.AmountMinor != SandboxAmountMinor ||
            command.Currency != SandboxCurrency ||
            command.SimulateDecline != order.SimulatePaymentDecline ||
            command.RequestedAtUtc != order.RequestedAtUtc)
            throw new InvalidDataException("Payment authorization command conflicts with checkout intent.");
    }

    private static void ValidateCommand(OutboxMessage outbox, CheckoutRequestedV1 request)
    {
        var command = JsonSerializer.Deserialize<InventorySaleRequestedV1>(outbox.PayloadJson, JsonOptions);
        if (command is null || command.MessageId != outbox.MessageId ||
            command.OrderId != request.OrderId || command.HoldId != request.HoldId ||
            command.BuyerId != request.BuyerId || command.RequestedAtUtc != request.RequestedAtUtc)
            throw new InvalidDataException("Existing Inventory command conflicts with checkout fact.");
    }

    private static DateTimeOffset Truncate(DateTimeOffset timestamp) =>
        timestamp.AddTicks(-(timestamp.Ticks % 10));
}

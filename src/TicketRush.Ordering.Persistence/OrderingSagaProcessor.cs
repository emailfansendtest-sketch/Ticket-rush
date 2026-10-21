using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketRush.BuildingBlocks.Contracts.Checkout;

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
        if (order.State != CheckoutState.Pending)
            throw new InvalidDataException("Terminal order cannot schedule a new sale.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var now = Truncate(clock.GetUtcNow());
            var commandId = Guid.CreateVersion7();
            var command = new InventorySaleRequestedV1(commandId, message.OrderId,
                message.HoldId, message.BuyerId, message.RequestedAtUtc);
            db.OutboxMessages.Add(new OutboxMessage
            {
                MessageId = commandId, OrderId = message.OrderId,
                MessageType = nameof(InventorySaleRequestedV1),
                PayloadJson = JsonSerializer.Serialize(command, JsonOptions),
                CreatedAtUtc = now
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
            existing = await db.OutboxMessages.AsNoTracking().SingleOrDefaultAsync(
                x => x.OrderId == message.OrderId && x.MessageType == nameof(InventorySaleRequestedV1), ct);
            if (existing is null) throw;
            ValidateCommand(existing, message);
            return SagaProcessOutcome.Duplicate;
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
                SELECT id, hold_id, buyer_id, idempotency_key, state, requested_at_utc
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

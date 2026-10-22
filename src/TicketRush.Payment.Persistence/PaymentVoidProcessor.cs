using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TicketRush.BuildingBlocks.Contracts.Payment;

namespace TicketRush.Payment.Persistence;

/// <summary>Idempotent, atomic simulated authorization void; never refunds/captures.</summary>
public sealed class PaymentVoidProcessor(PaymentDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PaymentProcessOutcome> ProcessAsync(PaymentVoidRequestedV1 command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.MessageId == Guid.Empty || command.OrderId == Guid.Empty ||
            command.BuyerId == Guid.Empty || command.AuthorizationCommandId == Guid.Empty ||
            command.RequestedAtUtc == default || command.RequestedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Invalid PaymentVoidRequestedV1 identity.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Lock the authoritative authorization first to serialize competing voids.
            var locked = await db.Receipts.FromSqlInterpolated($"""
                SELECT message_id, order_id, buyer_id, amount_minor, currency,
                       simulate_decline, requested_at_utc, processed_at_utc,
                       result_message_id, result_type, voided_at_utc
                FROM payment_receipts WHERE order_id = {command.OrderId} FOR UPDATE
                """).ToListAsync(ct);
            var authorization = locked.SingleOrDefault()
                ?? throw new InvalidDataException("Cannot void an unknown authorization.");
            if (authorization.MessageId != command.AuthorizationCommandId ||
                authorization.BuyerId != command.BuyerId ||
                authorization.RequestedAtUtc != Truncate(command.RequestedAtUtc) ||
                authorization.ResultType != nameof(PaymentAuthorizedV1))
                throw new InvalidDataException("Void command does not identify an authorized Payment.");

            var previous = await db.VoidReceipts.SingleOrDefaultAsync(
                x => x.OrderId == command.OrderId, ct);
            if (previous is not null)
            {
                if (previous.MessageId != command.MessageId ||
                    previous.BuyerId != command.BuyerId ||
                    previous.AuthorizationCommandId != command.AuthorizationCommandId)
                    throw new InvalidDataException("Conflicting Payment void identity.");
                // In case the previous result disappeared in transit, re-arm the
                // SAME outbox message. The publisher and Ordering inbox deduplicate.
                var outbox = await db.Outbox.SingleOrDefaultAsync(
                    x => x.MessageId == previous.ResultMessageId &&
                         x.OrderId == command.OrderId &&
                         x.MessageType == nameof(PaymentVoidedV1), ct)
                    ?? throw new InvalidOperationException("Payment void receipt lost its result outbox.");
                if (outbox.PublishedAtUtc is not null)
                {
                    outbox.PublishedAtUtc = null;
                    outbox.NextAttemptAtUtc = null;
                    outbox.LastError = null;
                    await db.SaveChangesAsync(ct);
                }
                await transaction.CommitAsync(ct);
                return PaymentProcessOutcome.Duplicate;
            }
            if (authorization.VoidedAtUtc is not null)
                throw new InvalidOperationException("Authorization is voided but has no void receipt.");

            var now = Truncate(clock.GetUtcNow());
            var resultId = Guid.CreateVersion7();
            authorization.VoidedAtUtc = now;
            db.VoidReceipts.Add(new PaymentVoidReceipt
            {
                MessageId = command.MessageId, OrderId = command.OrderId,
                BuyerId = command.BuyerId,
                AuthorizationCommandId = command.AuthorizationCommandId,
                ResultMessageId = resultId, ProcessedAtUtc = now
            });
            db.Outbox.Add(new PaymentOutboxMessage
            {
                MessageId = resultId, OrderId = command.OrderId,
                MessageType = nameof(PaymentVoidedV1), CreatedAtUtc = now,
                PayloadJson = JsonSerializer.Serialize(new PaymentVoidedV1(
                    resultId, command.OrderId, command.MessageId,
                    command.AuthorizationCommandId, now), JsonOptions)
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return PaymentProcessOutcome.Processed;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static DateTimeOffset Truncate(DateTimeOffset time) =>
        time.AddTicks(-(time.Ticks % 10));
}

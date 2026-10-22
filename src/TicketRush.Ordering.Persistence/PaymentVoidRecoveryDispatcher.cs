using Microsoft.EntityFrameworkCore;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.BuildingBlocks.Contracts.Payment;

namespace TicketRush.Ordering.Persistence;

public enum VoidRecoveryOutcome { None, RepublishScheduled, AlreadyQueued, NeedsAttention }

/// <summary>Repair missing Payment void delivery without changing Failed checkout state.</summary>
public sealed class PaymentVoidRecoveryDispatcher(
    OrderingDbContext db, TimeProvider clock, SagaRecoveryOptions options)
{
    public Guid? LastProcessedOrderId { get; private set; }

    public async Task<VoidRecoveryOutcome> RecoverNextAsync(CancellationToken ct)
    {
        options.Validate();
        LastProcessedOrderId = null;
        var now = Truncate(clock.GetUtcNow());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var candidates = await db.OrderIntents.FromSqlInterpolated($"""
            SELECT id, hold_id, buyer_id, idempotency_key, state, requested_at_utc,
                   payment_required, simulate_payment_decline,
                   recovery_attempts, next_recovery_at_utc, recovery_needs_attention
            FROM order_intents o
            WHERE state = 2 AND payment_required = TRUE
              AND recovery_needs_attention = FALSE
              AND next_recovery_at_utc IS NOT NULL
              AND next_recovery_at_utc <= {now}
              AND EXISTS (SELECT 1 FROM ordering_result_inbox r
                          WHERE r.order_id = o.id AND
                                r.message_type = {nameof(InventorySaleRejectedV1)})
              AND NOT EXISTS (SELECT 1 FROM ordering_void_inbox v WHERE v.order_id = o.id)
            ORDER BY next_recovery_at_utc, id
            LIMIT 1 FOR UPDATE OF o SKIP LOCKED
            """).ToListAsync(ct);
        if (candidates.Count == 0) return VoidRecoveryOutcome.None;
        var order = candidates[0];
        LastProcessedOrderId = order.Id;
        var command = await db.OutboxMessages.SingleOrDefaultAsync(x =>
            x.OrderId == order.Id && x.MessageType == nameof(PaymentVoidRequestedV1), ct);
        if (command is null)
        {
            order.RecoveryNeedsAttention = true;
            order.NextRecoveryAtUtc = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return VoidRecoveryOutcome.NeedsAttention;
        }
        var published = command.PublishedAtUtc is not null;
        if (published)
        {
            command.PublishedAtUtc = null;
            command.NextAttemptAtUtc = null;
            command.LastError = null;
        }
        order.RecoveryAttempts = checked(order.RecoveryAttempts + 1);
        if (order.RecoveryAttempts >= options.MaximumAttempts)
        {
            order.RecoveryNeedsAttention = true;
            order.NextRecoveryAtUtc = null;
        }
        else
        {
            var seconds = Math.Min(options.MaximumRetryDelay.TotalSeconds,
                options.RetryBaseDelay.TotalSeconds * Math.Pow(2, order.RecoveryAttempts - 1));
            order.NextRecoveryAtUtc = Truncate(now.AddSeconds(seconds));
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return published ? VoidRecoveryOutcome.RepublishScheduled :
            VoidRecoveryOutcome.AlreadyQueued;
    }
    private static DateTimeOffset Truncate(DateTimeOffset time) =>
        time.AddTicks(-(time.Ticks % 10));
}

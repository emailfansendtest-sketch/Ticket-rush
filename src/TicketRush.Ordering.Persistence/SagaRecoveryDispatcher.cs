using Microsoft.EntityFrameworkCore;
using TicketRush.BuildingBlocks.Contracts.Checkout;

namespace TicketRush.Ordering.Persistence;

/// <summary>
/// A timeout is NOT proof of a failed Inventory sale. Re-drive the stable
/// outbox message; the Inventory inbox and Ordering result inbox deduplicate.
/// Never change CheckoutState here.
/// </summary>
public enum SagaRecoveryOutcome
{
    None, CheckoutRepublishScheduled, InventoryRepublishScheduled,
    CheckoutAlreadyQueued, InventoryAlreadyQueued, NeedsAttention
}

public sealed record SagaRecoveryOptions(
    TimeSpan InitialTimeout, TimeSpan RetryBaseDelay,
    TimeSpan MaximumRetryDelay, int MaximumAttempts)
{
    public static SagaRecoveryOptions Default { get; } = new(
        TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(10), 5);

    public void Validate()
    {
        if (InitialTimeout <= TimeSpan.Zero || RetryBaseDelay <= TimeSpan.Zero ||
            MaximumRetryDelay < RetryBaseDelay || MaximumAttempts < 1 || MaximumAttempts > 100)
            throw new ArgumentOutOfRangeException(nameof(SagaRecoveryOptions),
                "Recovery intervals must be positive and attempts between 1 and 100.");
    }
}

public sealed class SagaRecoveryDispatcher(
    OrderingDbContext db, TimeProvider clock, SagaRecoveryOptions options)
{
    public Guid? LastProcessedOrderId { get; private set; }

    public async Task<SagaRecoveryOutcome> RecoverNextAsync(CancellationToken ct)
    {
        options.Validate();
        LastProcessedOrderId = null;
        var now = Truncate(clock.GetUtcNow());
        var cutoff = Truncate(now - options.InitialTimeout);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // No composed LINQ after FOR UPDATE SKIP LOCKED. Competing watchdog
        // replicas must not schedule retries for the same intent concurrently.
        var pending = await db.OrderIntents.FromSqlInterpolated($"""
            SELECT id, hold_id, buyer_id, idempotency_key, state, requested_at_utc,
                   recovery_attempts, next_recovery_at_utc, recovery_needs_attention
            FROM order_intents
            WHERE state = 0 AND recovery_needs_attention = FALSE
              AND requested_at_utc <= {cutoff}
              AND (next_recovery_at_utc IS NULL OR next_recovery_at_utc <= {now})
            ORDER BY requested_at_utc, id
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);

        if (pending.Count == 0)
            return SagaRecoveryOutcome.None;

        var order = pending[0];
        LastProcessedOrderId = order.Id;
        // The initial checkout fact is saved atomically with the order, so
        // it must exist. Preserve the existing event and its MessageId.
        var messages = await db.OutboxMessages
            .Where(x => x.OrderId == order.Id).ToListAsync(ct);
        var command = messages.SingleOrDefault(x =>
            x.MessageType == nameof(InventorySaleRequestedV1));
        var fact = messages.SingleOrDefault(x =>
            x.MessageType == nameof(CheckoutRequestedV1));
        if (fact is null)
        {
            // Corrupt/missing durable fact: no safe message to replay. Quarantine
            // this order operationally rather than starve all later pending rows.
            order.RecoveryAttempts = checked(order.RecoveryAttempts + 1);
            order.RecoveryNeedsAttention = true;
            order.NextRecoveryAtUtc = null;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return SagaRecoveryOutcome.NeedsAttention;
        }
        var chosen = command ?? fact;
        var wasPublished = chosen.PublishedAtUtc is not null;
        if (wasPublished)
        {
            chosen.PublishedAtUtc = null;
            chosen.NextAttemptAtUtc = null;
            chosen.LastError = null;
        }
        // Count recovery investigations even if the original outbox row is
        // already pending; this eventually surfaces a permanently stuck case.
        order.RecoveryAttempts = checked(order.RecoveryAttempts + 1);
        if (order.RecoveryAttempts >= options.MaximumAttempts)
        {
            // Operational alert only. Inventory may already have sold the seat.
            order.RecoveryNeedsAttention = true;
            order.NextRecoveryAtUtc = null;
        }
        else
        {
            var delaySeconds = Math.Min(options.MaximumRetryDelay.TotalSeconds,
                options.RetryBaseDelay.TotalSeconds *
                Math.Pow(2, order.RecoveryAttempts - 1));
            order.NextRecoveryAtUtc = Truncate(now.AddSeconds(delaySeconds));
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (order.RecoveryNeedsAttention) return SagaRecoveryOutcome.NeedsAttention;
        if (command is not null)
            return wasPublished ? SagaRecoveryOutcome.InventoryRepublishScheduled
                : SagaRecoveryOutcome.InventoryAlreadyQueued;
        return wasPublished ? SagaRecoveryOutcome.CheckoutRepublishScheduled
            : SagaRecoveryOutcome.CheckoutAlreadyQueued;
    }

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % 10));
}

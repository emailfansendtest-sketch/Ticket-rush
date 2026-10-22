using Microsoft.EntityFrameworkCore;

namespace TicketRush.Payment.Persistence;

public enum PaymentDispatchOutcome { Empty, Published, Deferred }

public sealed class PaymentOutboxDispatcher(
    PaymentDbContext db, IPaymentResultPublisher publisher, TimeProvider clock)
{
    public async Task<PaymentDispatchOutcome> DispatchNextAsync(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = Truncate(clock.GetUtcNow());
        var batch = await db.Outbox.FromSqlInterpolated($"""
            SELECT message_id, order_id, message_type, payload_json, created_at_utc,
                   published_at_utc, publish_attempts, next_attempt_at_utc, last_error
            FROM payment_outbox
            WHERE published_at_utc IS NULL
              AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= {now})
            ORDER BY created_at_utc, message_id
            LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        if (batch.Count == 0) return PaymentDispatchOutcome.Empty;
        var item = batch[0];
        item.PublishAttempts = checked(item.PublishAttempts + 1);
        Exception? failure = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await publisher.PublishAsync(item, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { failure = ex; }
        if (failure is not null)
        {
            var delay = Math.Min(60, 1 << Math.Min(item.PublishAttempts, 6));
            item.NextAttemptAtUtc = Truncate(clock.GetUtcNow().AddSeconds(delay));
            item.LastError = (failure.GetType().Name + ": " + failure.Message)[..Math.Min(1000,
                (failure.GetType().Name + ": " + failure.Message).Length)];
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return PaymentDispatchOutcome.Deferred;
        }
        item.PublishedAtUtc = Truncate(clock.GetUtcNow());
        item.NextAttemptAtUtc = null;
        item.LastError = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return PaymentDispatchOutcome.Published;
    }
    private static DateTimeOffset Truncate(DateTimeOffset time) =>
        time.AddTicks(-(time.Ticks % 10));
}

using Microsoft.EntityFrameworkCore;

namespace TicketRush.Infrastructure;

public enum InventoryResultDispatchOutcome { Empty, Published, Deferred }

/// <summary>
/// Selects one due outbox message while holding a PostgreSQL row lock until the
/// publisher is confirmed and the result is committed. Never promises exactly-once
/// delivery: a crash between broker confirm and DB commit can publish again.
/// </summary>
public sealed class InventoryResultOutboxDispatcher(
    BookingDbContext db, IInventoryResultPublisher publisher, TimeProvider clock)
{
    public async Task<InventoryResultDispatchOutcome> DispatchNextAsync(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = Truncate(clock.GetUtcNow());
        // Do not compose further LINQ operators around FOR UPDATE SKIP LOCKED.
        var candidates = await db.InventoryResultOutbox.FromSqlInterpolated($"""
            SELECT message_id, order_id, message_type, payload_json,
                   created_at_utc, published_at_utc, publish_attempts,
                   next_attempt_at_utc, last_error
            FROM inventory_result_outbox
            WHERE published_at_utc IS NULL
              AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= {now})
            ORDER BY created_at_utc, message_id
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        if (candidates.Count == 0)
            return InventoryResultDispatchOutcome.Empty;

        var message = candidates[0];
        message.PublishAttempts = checked(message.PublishAttempts + 1);
        Exception? publishError = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await publisher.PublishAsync(message, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Worker shutdown: transaction rolls back, message remains eligible.
            throw;
        }
        catch (Exception ex)
        {
            publishError = ex;
        }

        if (publishError is not null)
        {
            var seconds = Math.Min(60, 1 << Math.Min(message.PublishAttempts, 6));
            message.NextAttemptAtUtc = Truncate(clock.GetUtcNow().AddSeconds(seconds));
            var error = publishError.GetType().Name + ": " + publishError.Message;
            message.LastError = error.Length > 1000 ? error[..1000] : error;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return InventoryResultDispatchOutcome.Deferred;
        }

        message.PublishedAtUtc = Truncate(clock.GetUtcNow());
        message.NextAttemptAtUtc = null;
        message.LastError = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return InventoryResultDispatchOutcome.Published;
    }

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % 10));
}

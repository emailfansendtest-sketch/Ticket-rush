using Microsoft.EntityFrameworkCore;

namespace TicketRush.Ordering.Persistence;

public enum DispatchOutcome { Empty, Published, Deferred }

/// <summary>
/// Single-message DB claim + confirmed publish. PostgreSQL row locks ensure
/// multiple worker replicas do not concurrently claim the same outbox row.
/// A crash after broker confirmation but before DB commit can redeliver:
/// consumers MUST be idempotent by MessageId.
/// </summary>
public sealed class OutboxDispatcher(
    OrderingDbContext db, IOutboxPublisher publisher, TimeProvider clock)
{
    public async Task<DispatchOutcome> DispatchNextAsync(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = TruncateToMicroseconds(clock.GetUtcNow());

        // This SQL is intentionally PostgreSQL-specific. Do not append LINQ
        // operators that wrap FOR UPDATE inside another SELECT.
        var candidates = await db.OutboxMessages.FromSqlInterpolated($"""
            SELECT message_id, order_id, message_type, payload_json,
                   created_at_utc, published_at_utc, publish_attempts,
                   next_attempt_at_utc, last_error
            FROM outbox_messages
            WHERE published_at_utc IS NULL
              AND (next_attempt_at_utc IS NULL OR next_attempt_at_utc <= {now})
            ORDER BY created_at_utc, message_id
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);

        if (candidates.Count == 0)
            return DispatchOutcome.Empty;

        var message = candidates[0];
        message.PublishAttempts = checked(message.PublishAttempts + 1);

        Exception? publishError = null;
        try
        {
            // Every attempt has a finite upper bound; an ambiguous timeout is
            // retried using the same stable MessageId.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await publisher.PublishAsync(message, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown: leave the row untouched, so retry on next startup.
            throw;
        }
        catch (Exception ex)
        {
            publishError = ex;
        }

        if (publishError is not null)
        {
            // Ambiguous publish outcomes deliberately remain retryable.
            var seconds = Math.Min(60, 1 << Math.Min(message.PublishAttempts, 6));
            message.NextAttemptAtUtc = TruncateToMicroseconds(clock.GetUtcNow().AddSeconds(seconds));
            message.LastError = publishError.GetType().Name + ": " + publishError.Message;
            if (message.LastError.Length > 1000)
                message.LastError = message.LastError[..1000];
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return DispatchOutcome.Deferred;
        }

        // If the database commit fails after broker confirmation, the outbox
        // row stays pending and is published again. This is at-least-once.
        message.PublishedAtUtc = TruncateToMicroseconds(clock.GetUtcNow());
        message.NextAttemptAtUtc = null;
        message.LastError = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return DispatchOutcome.Published;
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % 10));
}

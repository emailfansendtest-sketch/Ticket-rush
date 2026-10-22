using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketRush.BuildingBlocks.Contracts.Notification;

namespace TicketRush.Notification.Persistence;

public enum NotificationProcessOutcome { Recorded, Duplicate }

/// <summary>
/// Consumes terminal checkout notifications idempotently. "Delivery" here is
/// a durable simulated local record, not a send through a real provider.
/// </summary>
public sealed class NotificationDeliveryProcessor(NotificationDbContext db, TimeProvider clock)
{
    public async Task<NotificationProcessOutcome> ProcessAsync(
        CheckoutNotificationRequestedV1 command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);

        var previous = await FindExistingAsync(command, ct);
        if (previous is not null) return NotificationProcessOutcome.Duplicate;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            db.Receipts.Add(new NotificationReceipt
            {
                MessageId = command.MessageId,
                OrderId = command.OrderId,
                BuyerId = command.BuyerId,
                Outcome = command.Outcome,
                ReasonCode = command.ReasonCode,
                OccurredAtUtc = ToMicroseconds(command.OccurredAtUtc),
                RecordedAtUtc = ToMicroseconds(clock.GetUtcNow())
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return NotificationProcessOutcome.Recorded;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            previous = await FindExistingAsync(command, ct);
            if (previous is null)
                throw new InvalidOperationException("Notification unique-key conflict was not a replay.", ex);
            return NotificationProcessOutcome.Duplicate;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<NotificationReceipt?> FindExistingAsync(
        CheckoutNotificationRequestedV1 command, CancellationToken ct)
    {
        var byMessage = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(
            x => x.MessageId == command.MessageId, ct);
        if (byMessage is not null)
        {
            CheckReplay(byMessage, command);
            return byMessage;
        }
        var byOrder = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(
            x => x.OrderId == command.OrderId, ct);
        if (byOrder is not null)
        {
            CheckReplay(byOrder, command);
            return byOrder;
        }
        return null;
    }

    private static void CheckReplay(NotificationReceipt receipt, CheckoutNotificationRequestedV1 cmd)
    {
        if (receipt.MessageId != cmd.MessageId || receipt.OrderId != cmd.OrderId ||
            receipt.BuyerId != cmd.BuyerId || receipt.Outcome != cmd.Outcome ||
            receipt.ReasonCode != cmd.ReasonCode ||
            receipt.OccurredAtUtc != ToMicroseconds(cmd.OccurredAtUtc))
            throw new InvalidDataException("Conflicting notification message or replay identity.");
    }

    private static void Validate(CheckoutNotificationRequestedV1 cmd)
    {
        if (cmd.MessageId == Guid.Empty || cmd.OrderId == Guid.Empty || cmd.BuyerId == Guid.Empty ||
            cmd.Outcome is not ("Completed" or "Failed") ||
            (cmd.Outcome == "Completed" && cmd.ReasonCode is not null) ||
            cmd.ReasonCode?.Length > 120 ||
            cmd.OccurredAtUtc == default || cmd.OccurredAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Invalid checkout notification command.");
    }

    private static DateTimeOffset ToMicroseconds(DateTimeOffset time) =>
        time.AddTicks(-(time.Ticks % 10));
}

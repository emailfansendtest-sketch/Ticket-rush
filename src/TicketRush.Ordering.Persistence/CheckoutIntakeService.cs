using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketRush.BuildingBlocks.Contracts.Checkout;

namespace TicketRush.Ordering.Persistence;

public enum CheckoutAdmissionKind { Accepted, Replayed, Invalid, Conflict }

public sealed record CheckoutAdmission(
    CheckoutAdmissionKind Kind, Guid? OrderId = null,
    CheckoutState? State = null, DateTimeOffset? RequestedAtUtc = null);

/// <summary>
/// Accepts a checkout into Ordering's own database, atomically with an outbox fact.
/// It NEVER sells inventory or claims payment success. Exposed by the Ordering HTTP API.
/// </summary>
public sealed class CheckoutIntakeService(OrderingDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<CheckoutAdmission> AcceptAsync(
        Guid holdId, Guid buyerId, string? idempotencyKey, CancellationToken ct)
    {
        if (holdId == Guid.Empty || buyerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            return new(CheckoutAdmissionKind.Invalid);
        }

        var existing = await db.OrderIntents.AsNoTracking().FirstOrDefaultAsync(
            x => x.BuyerId == buyerId && x.IdempotencyKey == idempotencyKey, ct);
        if (existing is not null)
            return FromExisting(existing, holdId);

        var heldOrder = await db.OrderIntents.AsNoTracking().AnyAsync(
            x => x.HoldId == holdId, ct);
        if (heldOrder)
            return new(CheckoutAdmissionKind.Conflict);

        // One transaction commits both the pending intent and its outbound fact.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var orderId = Guid.CreateVersion7();
            var messageId = Guid.CreateVersion7();
            var now = clock.GetUtcNow();
            // PostgreSQL timestamptz stores microseconds, not 100-ns .NET ticks.
            now = now.AddTicks(-(now.Ticks % 10));

            var intent = new OrderIntent
            {
                Id = orderId,
                HoldId = holdId,
                BuyerId = buyerId,
                IdempotencyKey = idempotencyKey!,
                State = CheckoutState.Pending,
                RequestedAtUtc = now
            };
            var message = new CheckoutRequestedV1(messageId, orderId, holdId, buyerId, now);
            db.OrderIntents.Add(intent);
            db.OutboxMessages.Add(new OutboxMessage
            {
                MessageId = messageId,
                OrderId = orderId,
                MessageType = nameof(CheckoutRequestedV1),
                PayloadJson = JsonSerializer.Serialize(message, JsonOptions),
                CreatedAtUtc = now
            });

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(CheckoutAdmissionKind.Accepted, orderId, CheckoutState.Pending, now);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            // A competing request won one of the unique indexes. Re-read the
            // buyer/key mapping after the failed transaction has rolled back.
            existing = await db.OrderIntents.AsNoTracking().FirstOrDefaultAsync(
                x => x.BuyerId == buyerId && x.IdempotencyKey == idempotencyKey, ct);
            return existing is not null
                ? FromExisting(existing, holdId)
                : new(CheckoutAdmissionKind.Conflict);
        }
        catch (DbUpdateException)
        {
            // A failed SaveChanges does not accept the tracked additions. The
            // transaction is rolled back when disposed, but without clearing
            // tracking a later call on this same context can attempt to write
            // the failed order/outbox entities again.
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static CheckoutAdmission FromExisting(OrderIntent existing, Guid holdId) =>
        existing.HoldId == holdId
            ? new(CheckoutAdmissionKind.Replayed, existing.Id, existing.State, existing.RequestedAtUtc)
            : new(CheckoutAdmissionKind.Conflict);
}

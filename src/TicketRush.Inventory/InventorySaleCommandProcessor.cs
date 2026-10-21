using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketRush.BuildingBlocks.Contracts.Checkout;
using TicketRush.Domain;
using TicketRush.Infrastructure;

namespace TicketRush.Inventory;

public enum InventoryCommandOutcome { Processed, Duplicate }

/// <summary>
/// Handles one sale command entirely within the Booking/Inventory PostgreSQL database.
/// Inbox, seat/hold changes, local sale record and resulting outbox message commit atomically.
/// This is a transitional Inventory-owned operation; the legacy Booking DB is retained.
/// </summary>
public sealed class InventorySaleCommandProcessor(
    BookingDbContext db, IInventoryCheckout inventory, TimeProvider clock)
{
    public async Task<InventoryCommandOutcome> ProcessAsync(
        InventorySaleRequestedV1 command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.MessageId == Guid.Empty || command.OrderId == Guid.Empty ||
            command.HoldId == Guid.Empty || command.BuyerId == Guid.Empty)
            throw new ArgumentException("Sale command must identify message, order, hold and buyer.");

        // Fast replay path. The unique constraints below remain authoritative under races.
        var replay = await FindReplayAsync(command, ct);
        if (replay is not null)
            return replay.Value;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var now = Truncate(clock.GetUtcNow());
            var receipt = new InventoryInboxMessage
            {
                MessageId = command.MessageId,
                OrderId = command.OrderId,
                HoldId = command.HoldId,
                BuyerId = command.BuyerId,
                ProcessedAtUtc = now,
                ResultMessageId = Guid.NewGuid()
            };
            // Claim first. PostgreSQL serializes competing inserts for the same
            // message/order, even when workers run in separate processes.
            db.InventoryInbox.Add(receipt);
            await db.SaveChangesAsync(ct);

            var preparation = await inventory.PrepareCheckoutAsync(
                command.HoldId, command.BuyerId, command.OrderId, ct);

            // The hold/seat locks were acquired before choosing the terminal result.
            string resultType;
            string payload;
            if (preparation.Status == CheckoutPreparationStatus.Ready)
            {
                var soldAt = Truncate(preparation.CheckedAtUtc
                    ?? throw new InvalidOperationException("Inventory did not supply sale time."));
                db.Orders.Add(new TicketOrder
                {
                    Id = command.OrderId,
                    HoldId = command.HoldId,
                    BuyerId = command.BuyerId,
                    IdempotencyKey = $"distributed:{command.OrderId:D}",
                    CreatedAtUtc = soldAt
                });
                resultType = nameof(InventorySaleCommittedV1);
                payload = JsonSerializer.Serialize(new InventorySaleCommittedV1(
                    receipt.ResultMessageId, command.OrderId, command.HoldId, soldAt));
            }
            else
            {
                // A prior legacy checkout can have sold the same hold. A different
                // orderId must never be represented as this command's success.
                resultType = nameof(InventorySaleRejectedV1);
                var reason = preparation.Status switch
                {
                    CheckoutPreparationStatus.NotFound => "HoldNotFound",
                    CheckoutPreparationStatus.Expired => "HoldExpired",
                    CheckoutPreparationStatus.AlreadyConfirmed => "HoldAlreadySold",
                    CheckoutPreparationStatus.AlreadyCheckedOut => "HoldAlreadyCheckedOut",
                    _ => throw new InvalidOperationException("Unexpected checkout preparation status.")
                };
                payload = JsonSerializer.Serialize(new InventorySaleRejectedV1(
                    receipt.ResultMessageId, command.OrderId, command.HoldId, reason, now));
            }

            receipt.ResultType = resultType;
            db.InventoryResultOutbox.Add(new InventoryResultOutbox
            {
                MessageId = receipt.ResultMessageId,
                OrderId = command.OrderId,
                MessageType = resultType,
                PayloadJson = payload,
                CreatedAtUtc = now
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return InventoryCommandOutcome.Processed;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            // Conflict can be a raced inbox claim or an existing sale record.
            // Only the former counts as an idempotent replay.
            replay = await FindReplayAsync(command, ct);
            if (replay is not null)
                return replay.Value;
            throw new InvalidDataException(
                "Inventory sale violates a uniqueness constraint without a matching inbox receipt.", ex);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<InventoryCommandOutcome?> FindReplayAsync(
        InventorySaleRequestedV1 command, CancellationToken ct)
    {
        // Check both identities separately. A query using MessageId OR OrderId
        // could hide a collision between two distinct existing receipts.
        var byMessageId = await db.InventoryInbox.AsNoTracking()
            .SingleOrDefaultAsync(x => x.MessageId == command.MessageId, ct);
        if (byMessageId is not null)
        {
            ValidateIdentity(byMessageId, command);
            return InventoryCommandOutcome.Duplicate;
        }
        var byOrderId = await db.InventoryInbox.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrderId == command.OrderId, ct);
        if (byOrderId is not null)
        {
            ValidateIdentity(byOrderId, command);
            return InventoryCommandOutcome.Duplicate;
        }
        return null;
    }

    private static void ValidateIdentity(
        InventoryInboxMessage seen, InventorySaleRequestedV1 command)
    {
        if (seen.OrderId != command.OrderId ||
            seen.HoldId != command.HoldId || seen.BuyerId != command.BuyerId)
            throw new InvalidDataException(
                "MessageId or OrderId was reused for another sale command.");
    }

    private static DateTimeOffset Truncate(DateTimeOffset value) =>
        value.AddTicks(-(value.Ticks % 10));
}

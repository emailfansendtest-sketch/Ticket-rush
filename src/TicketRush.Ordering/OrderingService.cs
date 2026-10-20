using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketRush.Domain;
using TicketRush.Infrastructure;
using TicketRush.Inventory;

namespace TicketRush.Ordering;

/// <summary>
/// Coordinates checkout idempotency and order persistence while delegating
/// all seat/hold locking and state changes to Inventory. Both modules share
/// one scoped BookingDbContext and ONE PostgreSQL transaction in Phase 1.
/// </summary>
public sealed class OrderingService(
    BookingDbContext db, IInventoryCheckout inventory) : IOrderingService
{
    public async Task<OrderView?> GetOrderAsync(
        Guid orderId, Guid buyerId, CancellationToken ct)
    {
        if (orderId == Guid.Empty || buyerId == Guid.Empty)
            return null;

        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == orderId && x.BuyerId == buyerId, ct);
        return order is null ? null : ToView(order);
    }

    public async Task<OperationResult<OrderView>> ConfirmAsync(
        Guid holdId, Guid buyerId, string? idempotencyKey, CancellationToken ct)
    {
        if (holdId == Guid.Empty || buyerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100)
        {
            return new(ResultKind.Invalid, Error: "Provide hold, buyer and idempotency key.");
        }

        var existingOrder = await db.Orders.AsNoTracking().FirstOrDefaultAsync(
            x => x.BuyerId == buyerId && x.IdempotencyKey == idempotencyKey, ct);

        if (existingOrder is not null)
        {
            return existingOrder.HoldId == holdId
                ? new(ResultKind.Replayed, ToView(existingOrder))
                : new(ResultKind.Conflict,
                    Error: "Idempotency key already used for another hold.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var orderId = Guid.CreateVersion7();

            // Inventory locks hold then seats, validates expiry, and modifies
            // tracked entities. It deliberately never saves or commits.
            var preparation = await inventory.PrepareCheckoutAsync(
                holdId, buyerId, orderId, ct);

            switch (preparation.Status)
            {
                case CheckoutPreparationStatus.NotFound:
                    return new(ResultKind.NotFound, Error: "Hold not found.");

                case CheckoutPreparationStatus.AlreadyConfirmed:
                {
                    // A second checkout may have passed the preliminary query
                    // before the first transaction committed. Recheck after the
                    // Inventory row lock, preserving the original retry behavior.
                    var confirmedOrder = await db.Orders.AsNoTracking()
                        .SingleOrDefaultAsync(x => x.HoldId == holdId, ct);

                    if (confirmedOrder is not null &&
                        confirmedOrder.BuyerId == buyerId &&
                        confirmedOrder.IdempotencyKey == idempotencyKey)
                    {
                        return new(ResultKind.Replayed, ToView(confirmedOrder));
                    }

                    return new(ResultKind.Conflict,
                        Error: "Hold was already checked out.");
                }

                case CheckoutPreparationStatus.AlreadyCheckedOut:
                    return new(ResultKind.Conflict,
                        Error: "Hold was already checked out.");

                case CheckoutPreparationStatus.Expired:
                    return new(ResultKind.Expired,
                        Error: "Hold expired or its seats were reassigned.");

                case CheckoutPreparationStatus.Ready:
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unexpected inventory checkout result: {preparation.Status}.");
            }

            // PostgreSQL timestamptz stores microseconds, while .NET ticks
            // represent 100 ns. Normalize BEFORE constructing the Created
            // response, so Created and Replayed return identical timestamps.
            var checkedAtUtc = preparation.CheckedAtUtc
                ?? throw new InvalidOperationException(
                    "Inventory did not provide the checkout timestamp.");
            var persistedTimestamp = checkedAtUtc.AddTicks(-(checkedAtUtc.Ticks % 10));

            var order = new TicketOrder
            {
                Id = orderId,
                BuyerId = buyerId,
                HoldId = holdId,
                IdempotencyKey = idempotencyKey!,
                CreatedAtUtc = persistedTimestamp
            };

            db.Orders.Add(order);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return new(ResultKind.Created, ToView(order));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();

            existingOrder = await db.Orders.AsNoTracking().FirstOrDefaultAsync(
                x => x.BuyerId == buyerId && x.IdempotencyKey == idempotencyKey, ct);

            return existingOrder?.HoldId == holdId
                ? new(ResultKind.Replayed, ToView(existingOrder))
                : new(ResultKind.Conflict,
                    Error: "Hold already checked out or key reused.");
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            return new(ResultKind.Conflict, Error: "Concurrent checkout; retry.");
        }
    }

    private static OrderView ToView(TicketOrder order) =>
        new(order.Id, order.HoldId, order.BuyerId, order.CreatedAtUtc);
}

using Microsoft.EntityFrameworkCore;
using Npgsql;

using TicketRush.Domain;
using TicketRush.Infrastructure.EntitySchemas;

namespace TicketRush.Infrastructure;

/// <summary>
/// Phase-1 checkout orchestration. The hold/seat-availability operations now belong to
/// TicketRush.Inventory; keep the checkout database transaction intact until Ordering
/// extraction. No distributed transaction or service boundary is introduced here.
/// </summary>
public sealed class BookingService(BookingDbContext db, TimeProvider clock)
{
    public async Task<OperationResult<OrderView>> ConfirmAsync(
        Guid holdId, Guid buyerId, string? idempotencyKey, CancellationToken ct)
    {
        if ( holdId == Guid.Empty || buyerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100 )
        {
            return new(ResultKind.Invalid, Error: "Provide hold, buyer and idempotency key.");
        }

        var existingOrder = await db.Orders.AsNoTracking().FirstOrDefaultAsync(
            x => x.BuyerId == buyerId && x.IdempotencyKey == idempotencyKey, ct);

        if ( existingOrder is not null )
        {
            return existingOrder.HoldId == holdId
                ? new( ResultKind.Replayed, ToView( existingOrder ) )
                : new( ResultKind.Conflict, Error: "Idempotency key already used for another hold." );
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        try
        {
            var holdSchema = db.GetEntitySchema<SeatHold>();
            string lockHoldSql = $$"""
                SELECT {{holdSchema.Columns}}
                FROM {{holdSchema.Table}}
                WHERE {{holdSchema.Column(x => x.Id)}} = {0} FOR UPDATE
                """;
            var holds = await db.Holds.FromSqlRaw(lockHoldSql, holdId).ToListAsync(ct);

            SeatHold? hold = holds.SingleOrDefault();

            if (hold is null || hold.BuyerId != buyerId)
            {
                return new( ResultKind.NotFound, Error: "Hold not found." );
            }

            if( hold.Status != HoldStatus.Active )
            {
                if( hold.Status == HoldStatus.Confirmed )
                {
                    var confirmedOrder = await db.Orders
                        .AsNoTracking()
                        .SingleOrDefaultAsync(
                            x => x.HoldId == holdId, ct );

                    if( confirmedOrder is not null &&
                        confirmedOrder.BuyerId == buyerId &&
                        confirmedOrder.IdempotencyKey == idempotencyKey )
                    {
                        return new(
                            ResultKind.Replayed,
                            ToView( confirmedOrder ) );
                    }
                }

                return new(
                    ResultKind.Conflict,
                    Error: "Hold was already checked out." );
            }
            var seatSchema = db.GetEntitySchema<SeatAllocation>();
            string lockHoldSeatsSql = $$"""
                SELECT {{seatSchema.Columns}}
                FROM {{seatSchema.Table}}
                WHERE {{seatSchema.Column(x => x.HoldId)}} = {0}
                ORDER BY {{seatSchema.Column(x => x.SeatCode)}} FOR UPDATE
                """;
            var seats = await db.Seats.FromSqlRaw(lockHoldSeatsSql, holdId).ToListAsync(ct);

            var now = clock.GetUtcNow();

            if (now >= hold.ExpiresAtUtc || seats.Count != hold.SeatCount ||
                seats.Any( s => s.Status != SeatStatus.Held || s.HeldUntilUtc <= now ))
            {
                return new( ResultKind.Expired, Error: "Hold expired or its seats were reassigned." );
            }

            var order = new TicketOrder
            {
                Id = Guid.CreateVersion7(), BuyerId = buyerId, HoldId = holdId,
                IdempotencyKey = idempotencyKey, CreatedAtUtc = now
            };

            db.Orders.Add(order);

            foreach (var seat in seats)
            {
                seat.Status = SeatStatus.Sold;
                seat.SoldOrderId = order.Id;
                seat.HeldUntilUtc = null;
            }

            hold.Status = HoldStatus.Confirmed;

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
                : new(ResultKind.Conflict, Error: "Hold already checked out or key reused.");
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

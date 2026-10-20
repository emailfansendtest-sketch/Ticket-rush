using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

using TicketRush.Domain;
using TicketRush.Infrastructure.EntitySchemas;

namespace TicketRush.Infrastructure;

// The PostgreSQL row locks and constraints—not process-local locks—protect correctness.
// This class is intentionally kept in the Phase-1 modular monolith. Split it only after
// integration tests prove the booking invariants.
public sealed class BookingService(BookingDbContext db, TimeProvider clock)
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(10);

    public async Task<OperationResult<HoldView>> HoldAsync(
        Guid eventId, Guid buyerId, string? idempotencyKey, string[]? requestedCodes,
        CancellationToken ct)
    {
        if ( eventId == Guid.Empty || buyerId == Guid.Empty ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 100 ||
            requestedCodes is not { Length: > 0 and <= 6 } ||
            requestedCodes.Any( x => string.IsNullOrWhiteSpace(x) ) )
        {
            return new(ResultKind.Invalid, Error: "Provide event, buyer, key and 1–6 seats.");
        }

        string[] codes = requestedCodes.Select( x => x.Trim().ToUpperInvariant() )
            .OrderBy( x => x, StringComparer.Ordinal ).ToArray();

        if (codes.Distinct( StringComparer.Ordinal ).Count() != codes.Length ||
            codes.Any( x => !Regex.IsMatch( x, "^[A-Z0-9-]{1,16}$" ) ))
        {
            return new( ResultKind.Invalid, Error: "Seat codes must be unique alphanumeric codes." );
        }

        string csv = string.Join(',', codes);

        string fingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{eventId:D}:{csv}")));

        var existing = await db.Holds.AsNoTracking().FirstOrDefaultAsync(
            x => x.BuyerId == buyerId && x.IdempotencyKey == idempotencyKey, ct);

        if (existing is not null)
        {
            return ReplayedHold( existing, fingerprint );
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Lock rows in deterministic order; two callers cannot hold the same seat.
            // This is a non-composable SQL query; don't add LINQ operators before ToListAsync.
            var seatSchema = db.GetEntitySchema<SeatAllocation>();
            
            // Column/table identifiers come from EF mappings. {0}/{1} remain
            // DbParameters; never interpolate user-provided values into raw SQL.
            string lockSeatsSql = $$"""
                SELECT {{seatSchema.Columns}}
                FROM {{seatSchema.Table}}
                WHERE {{seatSchema.Column(x => x.EventId)}} = {0}
                  AND {{seatSchema.Column(x => x.SeatCode)}} = ANY ({1})
                ORDER BY {{seatSchema.Column(x => x.SeatCode)}} FOR UPDATE
                """;
            
            var locked = await db.Seats.FromSqlRaw(lockSeatsSql, eventId, codes).ToListAsync(ct);

            if (locked.Count != codes.Length)
            {
                return new( ResultKind.NotFound, Error: "Event or seat not found." );
            }

            // Recheck idempotency after acquiring the seat locks.
            // A concurrent request may have created the same hold
            // while this transaction was waiting for those locks.
            existing = await db.Holds
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.BuyerId == buyerId &&
                         x.IdempotencyKey == idempotencyKey,
                    ct );

            if ( existing is not null )
            {
                return ReplayedHold( existing, fingerprint );
            }

            var now = clock.GetUtcNow();

            if ( locked.Any( x =>
                x.Status == SeatStatus.Sold ||
                ( x.Status == SeatStatus.Held &&
                 x.HeldUntilUtc > now ) ) )
            {
                return new(
                    ResultKind.Conflict,
                    Error: "One or more seats are unavailable." );
            }

            Guid holdId = Guid.CreateVersion7();

            // The fact that PostgreSQL's time preservation accuracy is 1 microsecond while .NET's is 100 nanoseconds,
            // so the value read from the database will be different from stored in the server's memory, unless corrected
            var expiresAtUtcForPostgreSQL = now.Add( HoldDuration );
            expiresAtUtcForPostgreSQL = expiresAtUtcForPostgreSQL.AddTicks( -(expiresAtUtcForPostgreSQL.Ticks % 10) );

             var hold = new SeatHold
            {
                Id = holdId, 
                EventId = eventId, 
                BuyerId = buyerId,
                IdempotencyKey = idempotencyKey, 
                RequestFingerprint = fingerprint,
                SeatCodesCsv = csv, 
                SeatCount = codes.Length,
                ExpiresAtUtc = expiresAtUtcForPostgreSQL, 
                Status = HoldStatus.Active
            };

            db.Holds.Add(hold);

            foreach (var seat in locked)
            {
                seat.Status = SeatStatus.Held;
                seat.HoldId = holdId;
                seat.HeldUntilUtc = hold.ExpiresAtUtc;
                seat.SoldOrderId = null;
            }

            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);

            return new(ResultKind.Created, ToView(hold));
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Parallel requests with the same idempotency key can race on its unique index.
            await tx.RollbackAsync(ct);

            db.ChangeTracker.Clear();

            existing = await db.Holds.AsNoTracking().FirstOrDefaultAsync(
                x => x.BuyerId == buyerId && x.IdempotencyKey == idempotencyKey, ct);

            return existing is null
                ? new(ResultKind.Conflict, Error: "Another request changed this booking.")
                : ReplayedHold(existing, fingerprint);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            return new(ResultKind.Conflict, Error: "Seat updated concurrently; retry.");
        }
    }

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

    private static OperationResult<HoldView> ReplayedHold(SeatHold hold, string fingerprint) =>
        hold.RequestFingerprint == fingerprint
            ? new(ResultKind.Replayed, ToView(hold))
            : new(ResultKind.Conflict, Error: "Idempotency key reused with different seats/event.");

    private static HoldView ToView(SeatHold hold) => new(hold.Id, hold.EventId, hold.BuyerId,
        hold.SeatCodesCsv.Split(','), hold.ExpiresAtUtc, hold.Status.ToString());

    private static OrderView ToView(TicketOrder order) =>
        new(order.Id, order.HoldId, order.BuyerId, order.CreatedAtUtc);
}

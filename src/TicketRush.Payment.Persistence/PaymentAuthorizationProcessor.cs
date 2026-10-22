using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketRush.BuildingBlocks.Contracts.Payment;

namespace TicketRush.Payment.Persistence;

public enum PaymentProcessOutcome { Processed, Duplicate }

/// <summary>Deterministic sandbox simulation: no payment gateway, no capture, no real funds.</summary>
public sealed class PaymentAuthorizationProcessor(PaymentDbContext db, TimeProvider clock)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PaymentProcessOutcome> ProcessAsync(
        PaymentAuthorizationRequestedV1 command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);
        var replay = await FindReplayAsync(command, ct);
        if (replay is not null) return replay.Value;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var now = Truncate(clock.GetUtcNow());
            var resultId = Guid.NewGuid();
            var resultType = command.SimulateDecline
                ? nameof(PaymentDeclinedV1) : nameof(PaymentAuthorizedV1);
            var payload = command.SimulateDecline
                ? JsonSerializer.Serialize(new PaymentDeclinedV1(
                    resultId, command.OrderId, "SimulatedDecline", now), JsonOptions)
                : JsonSerializer.Serialize(new PaymentAuthorizedV1(
                    resultId, command.OrderId, command.AmountMinor, command.Currency, now), JsonOptions);

            db.Receipts.Add(new PaymentReceipt
            {
                MessageId = command.MessageId, OrderId = command.OrderId,
                BuyerId = command.BuyerId, AmountMinor = command.AmountMinor,
                Currency = command.Currency, SimulateDecline = command.SimulateDecline,
                RequestedAtUtc = Truncate(command.RequestedAtUtc), ProcessedAtUtc = now,
                ResultMessageId = resultId, ResultType = resultType
            });
            db.Outbox.Add(new PaymentOutboxMessage
            {
                MessageId = resultId, OrderId = command.OrderId,
                MessageType = resultType, PayloadJson = payload,
                CreatedAtUtc = now
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return PaymentProcessOutcome.Processed;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            replay = await FindReplayAsync(command, ct);
            if (replay is not null) return replay.Value;
            throw new InvalidDataException("Conflicting Payment authorization identity.", ex);
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<PaymentProcessOutcome?> FindReplayAsync(
        PaymentAuthorizationRequestedV1 command, CancellationToken ct)
    {
        var byMessage = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(
            x => x.MessageId == command.MessageId, ct);
        if (byMessage is not null)
        {
            ValidateReplay(byMessage, command);
            await RearmResultAsync(byMessage, ct);
            return PaymentProcessOutcome.Duplicate;
        }
        var byOrder = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(
            x => x.OrderId == command.OrderId, ct);
        if (byOrder is not null)
        {
            ValidateReplay(byOrder, command);
            await RearmResultAsync(byOrder, ct);
            return PaymentProcessOutcome.Duplicate;
        }
        return null;
    }

    private async Task RearmResultAsync(PaymentReceipt receipt, CancellationToken ct)
    {
        var count = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE payment_outbox
            SET published_at_utc = NULL, next_attempt_at_utc = NULL, last_error = NULL
            WHERE message_id = {receipt.ResultMessageId}
              AND order_id = {receipt.OrderId}
              AND message_type = {receipt.ResultType}
              AND published_at_utc IS NOT NULL
            """, ct);
        if (count == 0 && !await db.Outbox.AsNoTracking().AnyAsync(x =>
                x.MessageId == receipt.ResultMessageId && x.OrderId == receipt.OrderId &&
                x.MessageType == receipt.ResultType, ct))
            throw new InvalidDataException("Payment receipt has no matching result outbox.");
    }

    private static void Validate(PaymentAuthorizationRequestedV1 command)
    {
        if (command.MessageId == Guid.Empty || command.OrderId == Guid.Empty ||
            command.BuyerId == Guid.Empty || command.AmountMinor <= 0 ||
            command.AmountMinor > 1_000_000_000 ||
            command.Currency is null || command.Currency.Length != 3 ||
            command.Currency.Any(x => x is < 'A' or > 'Z') ||
            command.RequestedAtUtc == default || command.RequestedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidDataException("Invalid simulated Payment authorization request.");
    }

    private static void ValidateReplay(PaymentReceipt receipt, PaymentAuthorizationRequestedV1 cmd)
    {
        if (receipt.MessageId != cmd.MessageId || receipt.OrderId != cmd.OrderId ||
            receipt.BuyerId != cmd.BuyerId || receipt.AmountMinor != cmd.AmountMinor ||
            receipt.Currency != cmd.Currency || receipt.SimulateDecline != cmd.SimulateDecline ||
            receipt.RequestedAtUtc != Truncate(cmd.RequestedAtUtc))
            throw new InvalidDataException("Payment command identity was reused with different data.");
    }

    private static DateTimeOffset Truncate(DateTimeOffset time) =>
        time.AddTicks(-(time.Ticks % 10));
}

using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace TicketRush.Inventory.Persistence;

/// <summary>Maintenance-only, transactionally copied Inventory snapshot from the old
/// Booking database to an empty, separately migrated Inventory database.</summary>
public static class InventoryDataCopier
{
    public static async Task CopyAsync(string sourceCs, string targetCs)
    {
    var a = new NpgsqlConnectionStringBuilder(sourceCs);
    var b = new NpgsqlConnectionStringBuilder(targetCs);
    if (a.Host == b.Host && a.Port == b.Port && a.Database == b.Database)
        throw new InvalidOperationException("Source and target database cannot be the same.");

    // The dedicated schema must already have been migrated. Never auto-create tables
    // while copying production-like data; schema deployment is a separate step.
    var options = new DbContextOptionsBuilder<InventoryDbContext>().UseNpgsql(targetCs).Options;
    await using (var ctx = new InventoryDbContext(options))
    {
        if ((await ctx.Database.GetPendingMigrationsAsync()).Any())
            throw new InvalidOperationException("Inventory schema migrations are pending; run inventory-migrate first.");
    }
    await using var source = new NpgsqlConnection(sourceCs);
    await using var target = new NpgsqlConnection(targetCs);
    await source.OpenAsync();
    await target.OpenAsync();
    // Copy under a consistent read snapshot; this is not a replacement for stopping writers.
    await using var sourceTx = await source.BeginTransactionAsync(IsolationLevel.RepeatableRead);
    await using var targetTx = await target.BeginTransactionAsync();

    (string Table, string Columns)[] copyPlan =
    [
        ("events", "id, name, starts_at_utc"),
        ("holds", "id, event_id, buyer_id, idempotency_key, request_fingerprint, seat_codes_csv, expires_at_utc, seat_count, status"),
        ("seats", "event_id, seat_code, status, hold_id, held_until_utc, sold_order_id"),
        ("inventory_sales", "order_id, hold_id, buyer_id, sold_at_utc"),
        ("inventory_inbox", "message_id, order_id, hold_id, buyer_id, processed_at_utc, result_message_id, result_type"),
        ("inventory_result_outbox", "message_id, order_id, message_type, payload_json, created_at_utc, published_at_utc, publish_attempts, next_attempt_at_utc, last_error")
    ];

    try
    {
        // Abort rather than merging into a previously used Inventory database.
        foreach (var (table, _) in copyPlan)
        {
            long count = await CountAsync(target, targetTx, table);
            if (count != 0)
                throw new InvalidOperationException($"Inventory target {table} is not empty ({count} rows); refusing to merge.");
        }

        foreach (var (table, columns) in copyPlan)
        {
            var expected = await CountAsync(source, sourceTx, table);
            // Explicit column lists omit xmin: its MVCC value is local to each DB.
            // COPY CSV quotes strings and JSON safely without loading tables into RAM.
            using (var reader = source.BeginTextExport(
                $"COPY (SELECT {columns} FROM public.{table}) TO STDOUT WITH (FORMAT CSV, HEADER TRUE)"))
            using (var writer = target.BeginTextImport(
                $"COPY public.{table} ({columns}) FROM STDIN WITH (FORMAT CSV, HEADER TRUE)"))
            {
                var buffer = new char[65536];
                int read;
                while ((read = await reader.ReadAsync(buffer.AsMemory())) != 0)
                    await writer.WriteAsync(buffer.AsMemory(0, read));
                await writer.FlushAsync();
            }
            long actual = await CountAsync(target, targetTx, table);
            if (actual != expected)
                throw new InvalidDataException($"Copy mismatch in {table}: source {expected}, target {actual}.");
            Console.WriteLine($"{table}: {actual} rows transferred.");
        }
        // No automatic deletion or modification of the old database.
        await targetTx.CommitAsync();
        Console.WriteLine("Inventory data cutover copy committed. Old Booking DB is intact.");
    }
    catch
    {
        await targetTx.RollbackAsync();
        throw;
    }

    }

    static async Task<long> CountAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string table)
    {
        await using var cmd = new NpgsqlCommand($"SELECT COUNT(*) FROM public.{table}", connection, tx);
        return (long)(await cmd.ExecuteScalarAsync() ?? throw new InvalidDataException("COUNT returned null"));
    }
}

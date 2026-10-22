using TicketRush.Inventory.Persistence;

// ONE-TIME MAINTENANCE-WINDOW MIGRATION. All writers, Ordering saga/outbox,
// Inventory consumers and publishers must be stopped before invoking this tool.
// The source Booking DB is never modified. The destination copy commits only
// after all tables have been imported and row counts match.
var sourceCs = Environment.GetEnvironmentVariable("ConnectionStrings__BookingDb")
    ?? throw new InvalidOperationException("Source BookingDb connection string is required.");
var targetCs = Environment.GetEnvironmentVariable("ConnectionStrings__InventoryDb")
    ?? throw new InvalidOperationException("Target InventoryDb connection string is required.");
if (!string.Equals(Environment.GetEnvironmentVariable("INVENTORY_CUTOVER_CONFIRMED"), "YES", StringComparison.Ordinal))
    throw new InvalidOperationException("Stop all writers, take backups, then set INVENTORY_CUTOVER_CONFIRMED=YES.");
await InventoryDataCopier.CopyAsync(sourceCs, targetCs);

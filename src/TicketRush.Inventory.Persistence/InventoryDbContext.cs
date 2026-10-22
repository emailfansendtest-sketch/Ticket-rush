using Microsoft.EntityFrameworkCore;
using TicketRush.Domain;
using TicketRush.Infrastructure;
using TicketRush.Infrastructure.DbConfigurations;

namespace TicketRush.Inventory.Persistence;

/// <summary>Inventory-owned EF Core model. Does not map the legacy orders aggregate.</summary>
public class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options) { }
    protected InventoryDbContext(DbContextOptions options) : base(options) { }

    public DbSet<TicketEvent> Events => Set<TicketEvent>();
    public DbSet<SeatAllocation> Seats => Set<SeatAllocation>();
    public DbSet<SeatHold> Holds => Set<SeatHold>();
    public DbSet<InventorySaleRecord> InventorySales => Set<InventorySaleRecord>();
    public DbSet<InventoryInboxMessage> InventoryInbox => Set<InventoryInboxMessage>();
    public DbSet<InventoryResultOutbox> InventoryResultOutbox => Set<InventoryResultOutbox>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new TicketEventConfiguration());
        builder.ApplyConfiguration(new SeatAllocationConfiguration());
        builder.ApplyConfiguration(new SeatHoldConfiguration());
        builder.ApplyConfiguration(new InventorySaleRecordConfiguration());
        builder.ApplyConfiguration(new InventoryInboxConfiguration());
        builder.ApplyConfiguration(new InventoryResultOutboxConfiguration());
    }
}

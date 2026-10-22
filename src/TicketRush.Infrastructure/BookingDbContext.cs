using Microsoft.EntityFrameworkCore;
using TicketRush.Domain;
using TicketRush.Inventory.Persistence;
using TicketRush.Infrastructure.DbConfigurations;

namespace TicketRush.Infrastructure;

/// <summary>Legacy-only compatibility DbContext, retaining the shared-transaction checkout tests.
/// Not used in the distributed Inventory deployments.</summary>
public sealed class BookingDbContext(DbContextOptions<BookingDbContext> options)
    : InventoryDbContext(options)
{
    public DbSet<TicketOrder> Orders => Set<TicketOrder>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfiguration(new TicketOrderConfiguration());
    }
}

using Microsoft.EntityFrameworkCore;

using TicketRush.Domain;
using TicketRush.Infrastructure.DbConfigurations;

namespace TicketRush.Infrastructure;

public sealed class BookingDbContext(DbContextOptions<BookingDbContext> options) : DbContext(options)
{
    public DbSet<TicketEvent> Events => Set<TicketEvent>();
    public DbSet<SeatAllocation> Seats => Set<SeatAllocation>();
    public DbSet<SeatHold> Holds => Set<SeatHold>();
    public DbSet<TicketOrder> Orders => Set<TicketOrder>();

    protected override void OnModelCreating( ModelBuilder modelBuilder )
    {
        base.OnModelCreating( modelBuilder );

        modelBuilder.ApplyConfiguration( new TicketEventConfiguration() );
        modelBuilder.ApplyConfiguration( new SeatAllocationConfiguration() );
        modelBuilder.ApplyConfiguration( new SeatHoldConfiguration() );
        modelBuilder.ApplyConfiguration( new TicketOrderConfiguration() );
    }
}

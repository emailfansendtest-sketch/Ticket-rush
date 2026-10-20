using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TicketRush.Domain;

namespace TicketRush.Infrastructure.DbConfigurations
{
    internal class SeatAllocationConfiguration : IEntityTypeConfiguration<SeatAllocation>
    {
        /// <inheritdoc />
        public void Configure( EntityTypeBuilder<SeatAllocation> builder )
        {
            builder.ToTable( "seats" );

            builder
                .HasKey( x => new { x.EventId, x.SeatCode } );

            builder
                .Property( x => x.EventId )
                .HasColumnName( "event_id" );

            builder
                .Property( x => x.SeatCode )
                .HasColumnName( "seat_code" )
                .HasMaxLength( 16 );

            builder
                .Property( x => x.Status )
                .HasColumnName( "status" )
                .HasConversion<int>();

            builder
                .Property( x => x.HoldId )
                .HasColumnName( "hold_id" );

            builder
                .Property( x => x.HeldUntilUtc )
                .HasColumnName( "held_until_utc" );

            builder
                .Property( x => x.SoldOrderId )
                .HasColumnName( "sold_order_id" );

            builder
                .Property( x => x.Version )
                .IsRowVersion();

            builder
                .HasIndex( x => x.HoldId );
        }
    }
}

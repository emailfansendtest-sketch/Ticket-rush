using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain;

namespace TicketRush.Infrastructure.DbConfigurations
{
    internal class TicketOrderConfiguration : IEntityTypeConfiguration<TicketOrder>
    {
        /// <inheritdoc />
        public void Configure( EntityTypeBuilder<TicketOrder> builder )
        {
            builder.ToTable( "orders" );

            builder
                .HasKey( x => x.Id );

            builder
                .Property( x => x.Id )
                .HasColumnName( "id" );

            builder
                .Property( x => x.HoldId )
                .HasColumnName( "hold_id" );

            builder
                .Property( x => x.BuyerId )
                .HasColumnName( "buyer_id" );

            builder
                .Property( x => x.IdempotencyKey )
                .HasColumnName( "idempotency_key" )
                .HasMaxLength( 100 );

            builder
                .Property( x => x.CreatedAtUtc )
                .HasColumnName( "created_at_utc" );

            builder
                .HasIndex( x => x.HoldId )
                .IsUnique();

            builder
                .HasIndex( x => new { x.BuyerId, x.IdempotencyKey } )
                .IsUnique();

            builder.HasOne<SeatHold>()
                .WithMany()
                .HasForeignKey( x => x.HoldId )
                .OnDelete( DeleteBehavior.NoAction );
        }
    }
}

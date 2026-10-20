using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain;

namespace TicketRush.Infrastructure.DbConfigurations
{
    internal class SeatHoldConfiguration : IEntityTypeConfiguration<SeatHold>
    {
        /// <inheritdoc />
        public void Configure( EntityTypeBuilder<SeatHold> builder )
        {
            builder.ToTable( "holds" );

            builder
                .HasKey( x => x.Id );

            builder
                .Property( x => x.Id )
                .HasColumnName( "id" );

            builder
                .Property( x => x.EventId )
                .HasColumnName( "event_id" );

            builder
                .Property( x => x.BuyerId )
                .HasColumnName( "buyer_id" );

            builder
                .Property( x => x.IdempotencyKey )
                .HasColumnName( "idempotency_key" )
                .HasMaxLength( 100 );

            builder
                .Property( x => x.RequestFingerprint )
                .HasColumnName( "request_fingerprint" )
                .HasMaxLength( 64 );

            builder
                .Property( x => x.SeatCodesCsv )
                .HasColumnName( "seat_codes_csv" );

            builder
                .Property( x => x.ExpiresAtUtc )
                .HasColumnName( "expires_at_utc" );

            builder
                .Property( x => x.SeatCount )
                .HasColumnName( "seat_count" );

            builder
                .Property( x => x.Status )
                .HasColumnName( "status" )
                .HasConversion<int>();

            builder
                .HasIndex( x => new { x.BuyerId, x.IdempotencyKey } )
                .IsUnique();
        }
    }
}

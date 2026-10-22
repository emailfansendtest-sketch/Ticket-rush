using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using TicketRush.Domain;

namespace TicketRush.Infrastructure.DbConfigurations
{
    internal class TicketEventConfiguration : IEntityTypeConfiguration<TicketEvent>
    {
        /// <inheritdoc />
        public void Configure( EntityTypeBuilder<TicketEvent> builder )
        {
            builder.ToTable( "events" );

            builder
                .HasKey( x => x.Id );

            builder
                .Property( x => x.Id )
                .HasColumnName( "id" );

            builder
                .Property( x => x.Name )
                .HasColumnName( "name" )
                .HasMaxLength( 200 )
                .IsRequired();

            builder
                .Property( x => x.StartsAtUtc )
                .HasColumnName( "starts_at_utc" );
        }
    }
}

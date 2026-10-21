using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TicketRush.Infrastructure.DbConfigurations;

internal sealed class InventoryResultOutboxConfiguration : IEntityTypeConfiguration<InventoryResultOutbox>
{
    public void Configure(EntityTypeBuilder<InventoryResultOutbox> b)
    {
        b.ToTable("inventory_result_outbox");
        b.HasKey(x => x.MessageId);
        b.Property(x => x.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        b.Property(x => x.OrderId).HasColumnName("order_id");
        b.Property(x => x.MessageType).HasColumnName("message_type").HasMaxLength(60).IsRequired();
        b.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        b.Property(x => x.PublishedAtUtc).HasColumnName("published_at_utc");
        b.HasIndex(x => x.OrderId).IsUnique();
    }
}

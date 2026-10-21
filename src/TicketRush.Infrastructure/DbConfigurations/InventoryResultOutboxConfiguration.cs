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
        b.Property(x => x.PublishAttempts).HasColumnName("publish_attempts");
        b.Property(x => x.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc");
        b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(1000);
        b.HasIndex(x => x.OrderId).IsUnique();
        b.HasIndex(x => new { x.PublishedAtUtc, x.NextAttemptAtUtc, x.CreatedAtUtc })
            .HasDatabaseName("IX_inventory_result_outbox_delivery");
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TicketRush.Infrastructure.DbConfigurations;

internal sealed class InventoryInboxConfiguration : IEntityTypeConfiguration<InventoryInboxMessage>
{
    public void Configure(EntityTypeBuilder<InventoryInboxMessage> b)
    {
        b.ToTable("inventory_inbox");
        b.HasKey(x => x.MessageId);
        b.Property(x => x.MessageId).HasColumnName("message_id").ValueGeneratedNever();
        b.Property(x => x.OrderId).HasColumnName("order_id");
        b.Property(x => x.HoldId).HasColumnName("hold_id");
        b.Property(x => x.BuyerId).HasColumnName("buyer_id");
        b.Property(x => x.ProcessedAtUtc).HasColumnName("processed_at_utc");
        b.Property(x => x.ResultMessageId).HasColumnName("result_message_id");
        b.Property(x => x.ResultType).HasColumnName("result_type").HasMaxLength(60).IsRequired();
        b.HasIndex(x => x.OrderId).IsUnique();
    }
}

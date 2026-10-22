using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TicketRush.Domain;

namespace TicketRush.Infrastructure.DbConfigurations;

internal sealed class InventorySaleRecordConfiguration : IEntityTypeConfiguration<InventorySaleRecord>
{
    public void Configure(EntityTypeBuilder<InventorySaleRecord> b)
    {
        b.ToTable("inventory_sales");
        b.HasKey(x => x.OrderId);
        b.Property(x => x.OrderId).HasColumnName("order_id").ValueGeneratedNever();
        b.Property(x => x.HoldId).HasColumnName("hold_id");
        b.Property(x => x.BuyerId).HasColumnName("buyer_id");
        b.Property(x => x.SoldAtUtc).HasColumnName("sold_at_utc");
        b.HasIndex(x => x.HoldId).IsUnique();
        b.HasOne<SeatHold>().WithMany().HasForeignKey(x => x.HoldId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

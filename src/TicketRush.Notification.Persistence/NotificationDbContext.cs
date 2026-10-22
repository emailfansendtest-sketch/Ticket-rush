using Microsoft.EntityFrameworkCore;

namespace TicketRush.Notification.Persistence;

public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options)
    : DbContext(options)
{
    public DbSet<NotificationReceipt> Receipts => Set<NotificationReceipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<NotificationReceipt>(b =>
        {
            b.ToTable("notification_receipts");
            b.HasKey(x => x.MessageId);
            b.Property(x => x.MessageId).HasColumnName("message_id").ValueGeneratedNever();
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.BuyerId).HasColumnName("buyer_id");
            b.Property(x => x.Outcome).HasColumnName("outcome").HasMaxLength(24).IsRequired();
            b.Property(x => x.ReasonCode).HasColumnName("reason_code").HasMaxLength(120);
            b.Property(x => x.OccurredAtUtc).HasColumnName("occurred_at_utc");
            b.Property(x => x.RecordedAtUtc).HasColumnName("recorded_at_utc");
            b.HasIndex(x => x.OrderId).IsUnique();
        });
    }
}

using Microsoft.EntityFrameworkCore;

namespace TicketRush.Payment.Persistence;

/// <summary>Payment's own database. No Booking/Ordering/Inventory table dependencies.</summary>
public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<PaymentReceipt> Receipts => Set<PaymentReceipt>();
    public DbSet<PaymentVoidReceipt> VoidReceipts => Set<PaymentVoidReceipt>();
    public DbSet<PaymentOutboxMessage> Outbox => Set<PaymentOutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PaymentReceipt>(b =>
        {
            b.ToTable("payment_receipts");
            b.HasKey(x => x.MessageId);
            b.Property(x => x.MessageId).HasColumnName("message_id").ValueGeneratedNever();
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.BuyerId).HasColumnName("buyer_id");
            b.Property(x => x.AmountMinor).HasColumnName("amount_minor");
            b.Property(x => x.Currency).HasColumnName("currency").HasMaxLength(3).IsRequired();
            b.Property(x => x.SimulateDecline).HasColumnName("simulate_decline");
            b.Property(x => x.RequestedAtUtc).HasColumnName("requested_at_utc");
            b.Property(x => x.ProcessedAtUtc).HasColumnName("processed_at_utc");
            b.Property(x => x.ResultMessageId).HasColumnName("result_message_id");
            b.Property(x => x.ResultType).HasColumnName("result_type").HasMaxLength(120).IsRequired();
            b.Property(x => x.VoidedAtUtc).HasColumnName("voided_at_utc");
            b.HasIndex(x => x.OrderId).IsUnique();
            b.HasIndex(x => x.ResultMessageId).IsUnique();
        });
        modelBuilder.Entity<PaymentVoidReceipt>(b =>
        {
            b.ToTable("payment_void_receipts");
            b.HasKey(x => x.MessageId);
            b.Property(x => x.MessageId).HasColumnName("message_id").ValueGeneratedNever();
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.BuyerId).HasColumnName("buyer_id");
            b.Property(x => x.AuthorizationCommandId).HasColumnName("authorization_command_id");
            b.Property(x => x.ResultMessageId).HasColumnName("result_message_id");
            b.Property(x => x.ProcessedAtUtc).HasColumnName("processed_at_utc");
            b.HasIndex(x => x.OrderId).IsUnique();
            b.HasIndex(x => x.ResultMessageId).IsUnique();
        });
        modelBuilder.Entity<PaymentOutboxMessage>(b =>
        {
            b.ToTable("payment_outbox");
            b.HasKey(x => x.MessageId);
            b.Property(x => x.MessageId).HasColumnName("message_id").ValueGeneratedNever();
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.MessageType).HasColumnName("message_type").HasMaxLength(120).IsRequired();
            b.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb").IsRequired();
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.Property(x => x.PublishedAtUtc).HasColumnName("published_at_utc");
            b.Property(x => x.PublishAttempts).HasColumnName("publish_attempts");
            b.Property(x => x.NextAttemptAtUtc).HasColumnName("next_attempt_at_utc");
            b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(1000);
            b.HasIndex(x => new { x.PublishedAtUtc, x.NextAttemptAtUtc, x.CreatedAtUtc });
            b.HasIndex(x => new { x.OrderId, x.MessageType }).IsUnique();
        });
    }
}

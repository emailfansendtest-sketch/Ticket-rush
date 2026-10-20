using Microsoft.EntityFrameworkCore;

namespace TicketRush.Ordering.Persistence;

/// <summary>Independent Ordering storage. No BookingDbContext or Inventory entity references.</summary>
public sealed class OrderingDbContext(DbContextOptions<OrderingDbContext> options) : DbContext(options)
{
    public DbSet<OrderIntent> OrderIntents => Set<OrderIntent>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<OrderIntent>(b =>
        {
            b.ToTable("order_intents", t =>
                t.HasCheckConstraint("CK_order_intents_state", "state IN (0,1,2)"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.HoldId).HasColumnName("hold_id");
            b.Property(x => x.BuyerId).HasColumnName("buyer_id");
            b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key")
                .HasMaxLength(100).IsRequired();
            b.Property(x => x.State).HasColumnName("state");
            b.Property(x => x.RequestedAtUtc).HasColumnName("requested_at_utc");
            b.HasIndex(x => new { x.BuyerId, x.IdempotencyKey }).IsUnique();
            b.HasIndex(x => x.HoldId).IsUnique();
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(x => x.MessageId);
            b.Property(x => x.MessageId).HasColumnName("message_id").ValueGeneratedNever();
            b.Property(x => x.OrderId).HasColumnName("order_id");
            b.Property(x => x.MessageType).HasColumnName("message_type")
                .HasMaxLength(120).IsRequired();
            b.Property(x => x.PayloadJson).HasColumnName("payload_json")
                .HasColumnType("jsonb").IsRequired();
            b.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            b.Property(x => x.PublishedAtUtc).HasColumnName("published_at_utc");
            b.Property(x => x.PublishAttempts).HasColumnName("publish_attempts");
            b.HasIndex(x => new { x.PublishedAtUtc, x.CreatedAtUtc });
            b.HasIndex(x => x.OrderId);
            b.HasIndex(x => new { x.OrderId, x.MessageType }).IsUnique();
            b.HasOne<OrderIntent>().WithMany().HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.NoAction);
        });
    }
}

using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TicketRush.Ordering.Persistence.Migrations;

[DbContext(typeof(OrderingDbContext))]
[Migration("20261021193000_OrderingSagaResultInbox")]
partial class OrderingSagaResultInbox
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasAnnotation("ProductVersion", "10.0.0")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);
        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

        modelBuilder.Entity("TicketRush.Ordering.Persistence.OrderIntent", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid").HasColumnName("id").ValueGeneratedNever();
            b.Property<Guid>("HoldId").HasColumnType("uuid").HasColumnName("hold_id");
            b.Property<Guid>("BuyerId").HasColumnType("uuid").HasColumnName("buyer_id");
            b.Property<string>("IdempotencyKey").IsRequired().HasMaxLength(100)
                .HasColumnType("character varying(100)").HasColumnName("idempotency_key");
            b.Property<int>("State").HasColumnType("integer").HasColumnName("state");
            b.Property<DateTimeOffset>("RequestedAtUtc")
                .HasColumnType("timestamp with time zone").HasColumnName("requested_at_utc");
            b.HasKey("Id");
            b.HasIndex("BuyerId", "IdempotencyKey").IsUnique();
            b.HasIndex("HoldId").IsUnique();
            b.ToTable("order_intents", (string)null, t =>
                t.HasCheckConstraint("CK_order_intents_state", "state IN (0,1,2)"));
        });

        modelBuilder.Entity("TicketRush.Ordering.Persistence.OrderingResultInboxMessage", b =>
        {
            b.Property<Guid>("MessageId").HasColumnType("uuid")
                .HasColumnName("message_id").ValueGeneratedNever();
            b.Property<Guid>("OrderId").HasColumnType("uuid").HasColumnName("order_id");
            b.Property<Guid>("HoldId").HasColumnType("uuid").HasColumnName("hold_id");
            b.Property<string>("MessageType").IsRequired().HasMaxLength(120)
                .HasColumnType("character varying(120)").HasColumnName("message_type");
            b.Property<string>("ReasonCode").HasMaxLength(120)
                .HasColumnType("character varying(120)").HasColumnName("reason_code");
            b.Property<DateTimeOffset>("ProcessedAtUtc")
                .HasColumnType("timestamp with time zone").HasColumnName("processed_at_utc");
            b.HasKey("MessageId");
            b.HasIndex("OrderId").IsUnique();
            b.ToTable("ordering_result_inbox", (string)null);
        });

        modelBuilder.Entity("TicketRush.Ordering.Persistence.OutboxMessage", b =>
        {
            b.Property<Guid>("MessageId").HasColumnType("uuid")
                .HasColumnName("message_id").ValueGeneratedNever();
            b.Property<Guid>("OrderId").HasColumnType("uuid").HasColumnName("order_id");
            b.Property<string>("MessageType").IsRequired().HasMaxLength(120)
                .HasColumnType("character varying(120)").HasColumnName("message_type");
            b.Property<string>("PayloadJson").IsRequired().HasColumnType("jsonb")
                .HasColumnName("payload_json");
            b.Property<DateTimeOffset>("CreatedAtUtc")
                .HasColumnType("timestamp with time zone").HasColumnName("created_at_utc");
            b.Property<DateTimeOffset?>("PublishedAtUtc")
                .HasColumnType("timestamp with time zone").HasColumnName("published_at_utc");
            b.Property<int>("PublishAttempts").HasColumnType("integer")
                .HasColumnName("publish_attempts");
            b.Property<DateTimeOffset?>("NextAttemptAtUtc")
                .HasColumnType("timestamp with time zone").HasColumnName("next_attempt_at_utc");
            b.Property<string>("LastError").HasMaxLength(1000)
                .HasColumnType("character varying(1000)").HasColumnName("last_error");
            b.HasKey("MessageId");
            b.HasIndex("OrderId");
            b.HasIndex("OrderId", "MessageType").IsUnique();
            b.HasIndex("PublishedAtUtc", "NextAttemptAtUtc", "CreatedAtUtc");
            b.ToTable("outbox_messages", (string)null);
        });

        modelBuilder.Entity("TicketRush.Ordering.Persistence.OutboxMessage", b =>
        {
            b.HasOne("TicketRush.Ordering.Persistence.OrderIntent", null)
                .WithMany()
                .HasForeignKey("OrderId")
                .OnDelete(DeleteBehavior.NoAction)
                .IsRequired();
        });
        modelBuilder.Entity("TicketRush.Ordering.Persistence.OrderingResultInboxMessage", b =>
        {
            b.HasOne("TicketRush.Ordering.Persistence.OrderIntent", null)
                .WithMany().HasForeignKey("OrderId")
                .OnDelete(DeleteBehavior.NoAction).IsRequired();
        });

#pragma warning restore 612, 618
    }
}

using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable
namespace TicketRush.Payment.Persistence.Migrations;

[DbContext(typeof(PaymentDbContext))]
[Migration("20261022034000_PaymentAuthorizationVoid")]
partial class PaymentAuthorizationVoid
{
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "10.0.0")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);
        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

        modelBuilder.Entity("TicketRush.Payment.Persistence.PaymentReceipt", b =>
        {
            b.Property<Guid>("MessageId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("message_id");
            b.Property<long>("AmountMinor").HasColumnType("bigint").HasColumnName("amount_minor");
            b.Property<Guid>("BuyerId").HasColumnType("uuid").HasColumnName("buyer_id");
            b.Property<string>("Currency").IsRequired().HasMaxLength(3).HasColumnType("character varying(3)").HasColumnName("currency");
            b.Property<Guid>("OrderId").HasColumnType("uuid").HasColumnName("order_id");
            b.Property<DateTimeOffset>("ProcessedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("processed_at_utc");
            b.Property<DateTimeOffset>("RequestedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("requested_at_utc");
            b.Property<Guid>("ResultMessageId").HasColumnType("uuid").HasColumnName("result_message_id");
            b.Property<string>("ResultType").IsRequired().HasMaxLength(120).HasColumnType("character varying(120)").HasColumnName("result_type");
            b.Property<bool>("SimulateDecline").HasColumnType("boolean").HasColumnName("simulate_decline");
            b.Property<DateTimeOffset?>("VoidedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("voided_at_utc");
            b.HasKey("MessageId");
            b.HasIndex("OrderId").IsUnique();
            b.HasIndex("ResultMessageId").IsUnique();
            b.ToTable("payment_receipts");
        });
        modelBuilder.Entity("TicketRush.Payment.Persistence.PaymentVoidReceipt", b =>
        {
            b.Property<Guid>("MessageId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("message_id");
            b.Property<Guid>("OrderId").HasColumnType("uuid").HasColumnName("order_id");
            b.Property<Guid>("BuyerId").HasColumnType("uuid").HasColumnName("buyer_id");
            b.Property<Guid>("AuthorizationCommandId").HasColumnType("uuid").HasColumnName("authorization_command_id");
            b.Property<Guid>("ResultMessageId").HasColumnType("uuid").HasColumnName("result_message_id");
            b.Property<DateTimeOffset>("ProcessedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("processed_at_utc");
            b.HasKey("MessageId");
            b.HasIndex("OrderId").IsUnique();
            b.HasIndex("ResultMessageId").IsUnique();
            b.ToTable("payment_void_receipts");
        });
        modelBuilder.Entity("TicketRush.Payment.Persistence.PaymentOutboxMessage", b =>
        {
            b.Property<Guid>("MessageId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("message_id");
            b.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("created_at_utc");
            b.Property<string>("LastError").HasMaxLength(1000).HasColumnType("character varying(1000)").HasColumnName("last_error");
            b.Property<string>("MessageType").IsRequired().HasMaxLength(120).HasColumnType("character varying(120)").HasColumnName("message_type");
            b.Property<DateTimeOffset?>("NextAttemptAtUtc").HasColumnType("timestamp with time zone").HasColumnName("next_attempt_at_utc");
            b.Property<Guid>("OrderId").HasColumnType("uuid").HasColumnName("order_id");
            b.Property<string>("PayloadJson").IsRequired().HasColumnType("jsonb").HasColumnName("payload_json");
            b.Property<DateTimeOffset?>("PublishedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("published_at_utc");
            b.Property<int>("PublishAttempts").HasColumnType("integer").HasColumnName("publish_attempts");
            b.HasKey("MessageId");
            b.HasIndex("OrderId", "MessageType").IsUnique();
            b.HasIndex("PublishedAtUtc", "NextAttemptAtUtc", "CreatedAtUtc");
            b.ToTable("payment_outbox");
        });
#pragma warning restore 612, 618
    }
}

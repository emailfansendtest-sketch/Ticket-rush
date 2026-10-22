using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using TicketRush.Notification.Persistence;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TicketRush.Notification.Persistence.Migrations;

[DbContext(typeof(NotificationDbContext))]
public partial class NotificationDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder.HasAnnotation("ProductVersion", "10.0.0")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);
        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

        modelBuilder.Entity("TicketRush.Notification.Persistence.NotificationReceipt", b =>
        {
            b.Property<Guid>("MessageId").ValueGeneratedNever().HasColumnType("uuid").HasColumnName("message_id");
            b.Property<Guid>("OrderId").HasColumnType("uuid").HasColumnName("order_id");
            b.Property<Guid>("BuyerId").HasColumnType("uuid").HasColumnName("buyer_id");
            b.Property<string>("Outcome").IsRequired().HasMaxLength(24).HasColumnType("character varying(24)").HasColumnName("outcome");
            b.Property<string>("ReasonCode").HasMaxLength(120).HasColumnType("character varying(120)").HasColumnName("reason_code");
            b.Property<DateTimeOffset>("OccurredAtUtc").HasColumnType("timestamp with time zone").HasColumnName("occurred_at_utc");
            b.Property<DateTimeOffset>("RecordedAtUtc").HasColumnType("timestamp with time zone").HasColumnName("recorded_at_utc");
            b.HasKey("MessageId");
            b.HasIndex("OrderId").IsUnique();
            b.ToTable("notification_receipts");
        });
#pragma warning restore 612, 618
    }
}

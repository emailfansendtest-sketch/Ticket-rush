using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Ordering.Persistence.Migrations;

public partial class OutboxDeliveryScheduling : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "next_attempt_at_utc", table: "outbox_messages",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "last_error", table: "outbox_messages",
            type: "character varying(1000)", maxLength: 1000, nullable: true);
        migrationBuilder.DropIndex(
            name: "IX_outbox_messages_published_at_utc_created_at_utc",
            table: "outbox_messages");
        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_published_at_utc_next_attempt_at_utc_created_at_utc",
            table: "outbox_messages",
            columns: new[] { "published_at_utc", "next_attempt_at_utc", "created_at_utc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_outbox_messages_published_at_utc_next_attempt_at_utc_created_at_utc",
            table: "outbox_messages");
        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_published_at_utc_created_at_utc",
            table: "outbox_messages",
            columns: new[] { "published_at_utc", "created_at_utc" });
        migrationBuilder.DropColumn(name: "next_attempt_at_utc", table: "outbox_messages");
        migrationBuilder.DropColumn(name: "last_error", table: "outbox_messages");
    }
}

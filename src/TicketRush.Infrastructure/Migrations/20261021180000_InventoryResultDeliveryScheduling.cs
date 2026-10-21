using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Infrastructure.Migrations;

[DbContext(typeof(BookingDbContext))]
[Migration("20261021180000_InventoryResultDeliveryScheduling")]
public sealed class InventoryResultDeliveryScheduling : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "publish_attempts", table: "inventory_result_outbox",
            type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "next_attempt_at_utc", table: "inventory_result_outbox",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "last_error", table: "inventory_result_outbox",
            type: "character varying(1000)", maxLength: 1000, nullable: true);
        migrationBuilder.CreateIndex(
            name: "IX_inventory_result_outbox_delivery", table: "inventory_result_outbox",
            columns: new[] { "published_at_utc", "next_attempt_at_utc", "created_at_utc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_inventory_result_outbox_delivery", table: "inventory_result_outbox");
        migrationBuilder.DropColumn(name: "publish_attempts", table: "inventory_result_outbox");
        migrationBuilder.DropColumn(name: "next_attempt_at_utc", table: "inventory_result_outbox");
        migrationBuilder.DropColumn(name: "last_error", table: "inventory_result_outbox");
    }
}

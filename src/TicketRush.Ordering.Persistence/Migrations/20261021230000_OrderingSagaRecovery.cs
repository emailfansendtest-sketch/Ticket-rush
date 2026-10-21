using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace TicketRush.Ordering.Persistence.Migrations;

public partial class OrderingSagaRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "recovery_attempts", table: "order_intents",
            type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "next_recovery_at_utc", table: "order_intents",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "recovery_needs_attention", table: "order_intents",
            type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.CreateIndex(
            name: "IX_order_intents_recovery_due",
            table: "order_intents",
            columns: new[] { "state", "recovery_needs_attention", "next_recovery_at_utc", "requested_at_utc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_order_intents_recovery_due",
            table: "order_intents");
        migrationBuilder.DropColumn(name: "recovery_attempts", table: "order_intents");
        migrationBuilder.DropColumn(name: "next_recovery_at_utc", table: "order_intents");
        migrationBuilder.DropColumn(name: "recovery_needs_attention", table: "order_intents");
    }
}

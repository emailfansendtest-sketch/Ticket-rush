using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Infrastructure.Migrations;

[DbContext(typeof(BookingDbContext))]
[Migration("20261021040000_InventoryInboxAndOutbox")]
public sealed class InventoryInboxAndOutbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "inventory_inbox",
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                buyer_id = table.Column<Guid>(type: "uuid", nullable: false),
                processed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                result_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                result_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_inventory_inbox", x => x.message_id));
        migrationBuilder.CreateIndex(
            name: "IX_inventory_inbox_order_id", table: "inventory_inbox",
            column: "order_id", unique: true);

        migrationBuilder.CreateTable(
            name: "inventory_result_outbox",
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                message_type = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                payload_json = table.Column<string>(type: "jsonb", nullable: false),
                created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                published_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_inventory_result_outbox", x => x.message_id));
        migrationBuilder.CreateIndex(
            name: "IX_inventory_result_outbox_order_id", table: "inventory_result_outbox",
            column: "order_id", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("inventory_result_outbox");
        migrationBuilder.DropTable("inventory_inbox");
    }
}

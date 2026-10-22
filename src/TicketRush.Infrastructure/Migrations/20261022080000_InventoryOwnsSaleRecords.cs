using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Infrastructure.Migrations;

[DbContext(typeof(BookingDbContext))]
[Migration("20261022080000_InventoryOwnsSaleRecords")]
public sealed class InventoryOwnsSaleRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "inventory_sales",
            columns: table => new
            {
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                buyer_id = table.Column<Guid>(type: "uuid", nullable: false),
                sold_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_inventory_sales", x => x.order_id);
                table.ForeignKey(
                    name: "FK_inventory_sales_holds_hold_id",
                    column: x => x.hold_id, principalTable: "holds", principalColumn: "id",
                    onDelete: ReferentialAction.NoAction);
            });
        migrationBuilder.CreateIndex(
            name: "IX_inventory_sales_hold_id", table: "inventory_sales",
            column: "hold_id", unique: true);

        // Historical asynchronous Inventory successes were incorrectly persisted
        // as legacy TicketOrder rows. Copy their sale evidence before switching
        // new asynchronous writes to inventory_sales. This is idempotent if a
        // development migration is retried manually; old orders remain intact.
        migrationBuilder.Sql("""
            INSERT INTO inventory_sales (order_id, hold_id, buyer_id, sold_at_utc)
            SELECT id, hold_id, buyer_id, created_at_utc
            FROM orders
            WHERE idempotency_key = 'distributed:' || id::text
            ON CONFLICT (order_id) DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "inventory_sales");
    }
}

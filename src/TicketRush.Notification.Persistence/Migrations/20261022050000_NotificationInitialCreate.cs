using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Notification.Persistence.Migrations;

public partial class NotificationInitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "notification_receipts",
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                buyer_id = table.Column<Guid>(type: "uuid", nullable: false),
                outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                reason_code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                recorded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_notification_receipts", x => x.message_id));
        migrationBuilder.CreateIndex("IX_notification_receipts_order_id",
            "notification_receipts", "order_id", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("notification_receipts");
}

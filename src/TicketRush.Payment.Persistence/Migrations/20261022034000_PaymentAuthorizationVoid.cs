using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace TicketRush.Payment.Persistence.Migrations;

public partial class PaymentAuthorizationVoid : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(name: "voided_at_utc", table: "payment_receipts",
            type: "timestamp with time zone", nullable: true);
        migrationBuilder.DropIndex(name: "IX_payment_outbox_order_id", table: "payment_outbox");
        migrationBuilder.CreateIndex(name: "IX_payment_outbox_order_id_message_type",
            table: "payment_outbox", columns: new[] { "order_id", "message_type" }, unique: true);
        migrationBuilder.CreateTable(
            name: "payment_void_receipts",
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                buyer_id = table.Column<Guid>(type: "uuid", nullable: false),
                authorization_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                result_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                processed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_payment_void_receipts", x => x.message_id));
        migrationBuilder.CreateIndex("IX_payment_void_receipts_order_id",
            "payment_void_receipts", "order_id", unique: true);
        migrationBuilder.CreateIndex("IX_payment_void_receipts_result_message_id",
            "payment_void_receipts", "result_message_id", unique: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("payment_void_receipts");
        migrationBuilder.DropIndex("IX_payment_outbox_order_id_message_type", "payment_outbox");
        migrationBuilder.CreateIndex(name: "IX_payment_outbox_order_id", table: "payment_outbox",
            column: "order_id", unique: true);
        migrationBuilder.DropColumn("voided_at_utc", "payment_receipts");
    }
}

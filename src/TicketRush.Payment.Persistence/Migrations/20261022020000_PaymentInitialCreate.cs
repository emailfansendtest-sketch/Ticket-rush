using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Payment.Persistence.Migrations;

public partial class PaymentInitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "payment_receipts",
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                buyer_id = table.Column<Guid>(type: "uuid", nullable: false),
                amount_minor = table.Column<long>(type: "bigint", nullable: false),
                currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                simulate_decline = table.Column<bool>(type: "boolean", nullable: false),
                requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                processed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                result_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                result_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_payment_receipts", x => x.message_id));
        migrationBuilder.CreateTable(
            name: "payment_outbox",
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                message_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                payload_json = table.Column<string>(type: "jsonb", nullable: false),
                created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                published_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                publish_attempts = table.Column<int>(type: "integer", nullable: false),
                next_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                last_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_payment_outbox", x => x.message_id));
        migrationBuilder.CreateIndex("IX_payment_receipts_order_id", "payment_receipts", "order_id", unique: true);
        migrationBuilder.CreateIndex("IX_payment_receipts_result_message_id", "payment_receipts", "result_message_id", unique: true);
        migrationBuilder.CreateIndex("IX_payment_outbox_order_id", "payment_outbox", "order_id", unique: true);
        migrationBuilder.CreateIndex("IX_payment_outbox_published_at_utc_next_attempt_at_utc_created_at_utc",
            "payment_outbox", new[] { "published_at_utc", "next_attempt_at_utc", "created_at_utc" });
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("payment_outbox");
        migrationBuilder.DropTable("payment_receipts");
    }
}

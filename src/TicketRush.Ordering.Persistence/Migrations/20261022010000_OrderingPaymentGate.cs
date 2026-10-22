using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace TicketRush.Ordering.Persistence.Migrations;

public partial class OrderingPaymentGate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "payment_required", table: "order_intents",
            type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<bool>(name: "simulate_payment_decline", table: "order_intents",
            type: "boolean", nullable: false, defaultValue: false);
        migrationBuilder.CreateTable(
            name: "ordering_payment_inbox",
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                message_type = table.Column<string>(type: "character varying(120)",
                    maxLength: 120, nullable: false),
                reason_code = table.Column<string>(type: "character varying(120)",
                    maxLength: 120, nullable: true),
                processed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone",
                    nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ordering_payment_inbox", x => x.message_id);
                table.ForeignKey("FK_ordering_payment_inbox_order_intents_order_id",
                    x => x.order_id, "order_intents", "id", onDelete: ReferentialAction.NoAction);
            });
        migrationBuilder.CreateIndex(name: "IX_ordering_payment_inbox_order_id",
            table: "ordering_payment_inbox", column: "order_id", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ordering_payment_inbox");
        migrationBuilder.DropColumn(name: "payment_required", table: "order_intents");
        migrationBuilder.DropColumn(name: "simulate_payment_decline", table: "order_intents");
    }
}

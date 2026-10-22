using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace TicketRush.Ordering.Persistence.Migrations;

public partial class OrderingPaymentVoidInbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ordering_void_inbox",
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                void_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                authorization_command_id = table.Column<Guid>(type: "uuid", nullable: false),
                processed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ordering_void_inbox", x => x.message_id);
                table.ForeignKey("FK_ordering_void_inbox_order_intents_order_id",
                    x => x.order_id, "order_intents", "id", onDelete: ReferentialAction.NoAction);
            });
        migrationBuilder.CreateIndex("IX_ordering_void_inbox_order_id",
            "ordering_void_inbox", "order_id", unique: true);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("ordering_void_inbox");
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace TicketRush.Ordering.Persistence.Migrations;

public partial class OrderingSagaResultInbox : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ordering_result_inbox",
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                message_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                reason_code = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                processed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ordering_result_inbox", x => x.message_id);
                table.ForeignKey("FK_ordering_result_inbox_order_intents_order_id",
                    x => x.order_id, "order_intents", "id", onDelete: ReferentialAction.NoAction);
            });
        migrationBuilder.CreateIndex(
            name: "IX_ordering_result_inbox_order_id", table: "ordering_result_inbox",
            column: "order_id", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "ordering_result_inbox");
    }
}

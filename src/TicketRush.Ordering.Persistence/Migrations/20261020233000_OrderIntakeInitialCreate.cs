using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Ordering.Persistence.Migrations;

public partial class OrderIntakeInitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "order_intents",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                buyer_id = table.Column<Guid>(type: "uuid", nullable: false),
                idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                state = table.Column<int>(type: "integer", nullable: false),
                requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_order_intents", x => x.id);
                table.CheckConstraint("CK_order_intents_state", "state IN (0,1,2)");
            });

        migrationBuilder.CreateTable(
            name: "outbox_messages",
            columns: table => new
            {
                message_id = table.Column<Guid>(type: "uuid", nullable: false),
                order_id = table.Column<Guid>(type: "uuid", nullable: false),
                message_type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                payload_json = table.Column<string>(type: "jsonb", nullable: false),
                created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                published_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                publish_attempts = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_outbox_messages", x => x.message_id);
                table.ForeignKey("FK_outbox_messages_order_intents_order_id",
                    x => x.order_id, "order_intents", "id", onDelete: ReferentialAction.NoAction);
            });

        migrationBuilder.CreateIndex(
            name: "IX_order_intents_buyer_id_idempotency_key", table: "order_intents",
            columns: new[] { "buyer_id", "idempotency_key" }, unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_order_intents_hold_id", table: "order_intents",
            column: "hold_id", unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_order_id", table: "outbox_messages",
            column: "order_id");
        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_order_id_message_type", table: "outbox_messages",
            columns: new[] { "order_id", "message_type" }, unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_outbox_messages_published_at_utc_created_at_utc", table: "outbox_messages",
            columns: new[] { "published_at_utc", "created_at_utc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "outbox_messages");
        migrationBuilder.DropTable(name: "order_intents");
    }
}

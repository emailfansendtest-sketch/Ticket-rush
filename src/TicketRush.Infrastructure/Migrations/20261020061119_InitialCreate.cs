using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketRush.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    starts_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "holds",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    buyer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    seat_codes_csv = table.Column<string>(type: "text", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    seat_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holds", x => x.id);
                    table.CheckConstraint("CK_holds_seat_count", "seat_count BETWEEN 1 AND 6");
                    table.CheckConstraint("CK_holds_status", "status IN (0,1)");
                    table.ForeignKey(
                        name: "FK_holds_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "seats",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    seat_code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    hold_id = table.Column<Guid>(type: "uuid", nullable: true),
                    held_until_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    sold_order_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seats", x => new { x.event_id, x.seat_code });
                    table.CheckConstraint("CK_seats_status", "status IN (0,1,2)");
                    table.ForeignKey(
                        name: "FK_seats_events_event_id",
                        column: x => x.event_id,
                        principalTable: "events",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    hold_id = table.Column<Guid>(type: "uuid", nullable: false),
                    buyer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.id);
                    table.ForeignKey(
                        name: "FK_orders_holds_hold_id",
                        column: x => x.hold_id,
                        principalTable: "holds",
                        principalColumn: "id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_holds_buyer_id_idempotency_key",
                table: "holds",
                columns: new[] { "buyer_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_holds_event_id",
                table: "holds",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_buyer_id_idempotency_key",
                table: "orders",
                columns: new[] { "buyer_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_hold_id",
                table: "orders",
                column: "hold_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_seats_hold_id",
                table: "seats",
                column: "hold_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "seats");

            migrationBuilder.DropTable(
                name: "holds");

            migrationBuilder.DropTable(
                name: "events");
        }
    }
}

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace TicketRush.Inventory.Persistence.Migrations;

[DbContext(typeof(InventoryDbContext))]
[Migration("20261022190000_InventoryInitialSchema")]
public sealed class InventoryInitialSchema : Migration
{
    protected override void Up(MigrationBuilder builder)
    {
        // Explicit SQL to preserve the physical schema of the tested transitional
        // database. PostgreSQL creates its xmin system column automatically.
        builder.Sql("""
            CREATE TABLE events (
                id uuid CONSTRAINT "PK_events" PRIMARY KEY,
                name varchar(200) NOT NULL,
                starts_at_utc timestamptz NOT NULL
            );
            CREATE TABLE holds (
                id uuid CONSTRAINT "PK_holds" PRIMARY KEY,
                event_id uuid NOT NULL CONSTRAINT "FK_holds_events_event_id" REFERENCES events(id) ON DELETE NO ACTION,
                buyer_id uuid NOT NULL,
                idempotency_key varchar(100) NOT NULL,
                request_fingerprint varchar(64) NOT NULL,
                seat_codes_csv text NOT NULL,
                expires_at_utc timestamptz NOT NULL,
                seat_count integer NOT NULL,
                status integer NOT NULL,
                CONSTRAINT "CK_holds_seat_count" CHECK (seat_count BETWEEN 1 AND 6),
                CONSTRAINT "CK_holds_status" CHECK (status IN (0,1))
            );
            CREATE UNIQUE INDEX "IX_holds_buyer_id_idempotency_key"
                ON holds (buyer_id, idempotency_key);
            CREATE INDEX "IX_holds_event_id" ON holds (event_id);
            CREATE TABLE seats (
                event_id uuid NOT NULL CONSTRAINT "FK_seats_events_event_id" REFERENCES events(id) ON DELETE NO ACTION,
                seat_code varchar(16) NOT NULL,
                status integer NOT NULL,
                hold_id uuid NULL,
                held_until_utc timestamptz NULL,
                sold_order_id uuid NULL,
                CONSTRAINT "PK_seats" PRIMARY KEY (event_id, seat_code),
                CONSTRAINT "CK_seats_status" CHECK (status IN (0,1,2))
            );
            CREATE INDEX ix_seats_hold_id ON seats (hold_id);
            CREATE TABLE inventory_sales (
                order_id uuid CONSTRAINT "PK_inventory_sales" PRIMARY KEY,
                hold_id uuid NOT NULL CONSTRAINT "FK_inventory_sales_holds_hold_id" REFERENCES holds(id) ON DELETE NO ACTION,
                buyer_id uuid NOT NULL,
                sold_at_utc timestamptz NOT NULL
            );
            CREATE UNIQUE INDEX "IX_inventory_sales_hold_id" ON inventory_sales (hold_id);
            CREATE TABLE inventory_inbox (
                message_id uuid CONSTRAINT "PK_inventory_inbox" PRIMARY KEY,
                order_id uuid NOT NULL,
                hold_id uuid NOT NULL,
                buyer_id uuid NOT NULL,
                processed_at_utc timestamptz NOT NULL,
                result_message_id uuid NOT NULL,
                result_type varchar(60) NOT NULL
            );
            CREATE UNIQUE INDEX "IX_inventory_inbox_order_id" ON inventory_inbox (order_id);
            CREATE TABLE inventory_result_outbox (
                message_id uuid CONSTRAINT "PK_inventory_result_outbox" PRIMARY KEY,
                order_id uuid NOT NULL,
                message_type varchar(60) NOT NULL,
                payload_json jsonb NOT NULL,
                created_at_utc timestamptz NOT NULL,
                published_at_utc timestamptz NULL,
                publish_attempts integer NOT NULL DEFAULT 0,
                next_attempt_at_utc timestamptz NULL,
                last_error varchar(1000) NULL
            );
            CREATE UNIQUE INDEX "IX_inventory_result_outbox_order_id"
                ON inventory_result_outbox (order_id);
            CREATE INDEX "IX_inventory_result_outbox_delivery"
                ON inventory_result_outbox
                (published_at_utc, next_attempt_at_utc, created_at_utc);
            """);
    }

    protected override void Down(MigrationBuilder builder)
    {
        builder.Sql("""
            DROP TABLE inventory_result_outbox;
            DROP TABLE inventory_inbox;
            DROP TABLE inventory_sales;
            DROP TABLE seats;
            DROP TABLE holds;
            DROP TABLE events;
            """);
    }
}

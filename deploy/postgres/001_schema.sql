-- DEVELOPMENT BOOTSTRAP ONLY. Move to EF Core migrations before Phase 2.
CREATE TABLE events (
  id uuid PRIMARY KEY,
  name varchar(200) NOT NULL,
  starts_at_utc timestamptz NOT NULL
);
CREATE TABLE seats (
  event_id uuid NOT NULL REFERENCES events(id),
  seat_code varchar(16) NOT NULL,
  status integer NOT NULL DEFAULT 0 CHECK (status IN (0,1,2)),
  hold_id uuid NULL,
  held_until_utc timestamptz NULL,
  sold_order_id uuid NULL,
  PRIMARY KEY (event_id, seat_code)
);
CREATE INDEX ix_seats_hold_id ON seats(hold_id);
CREATE TABLE holds (
  id uuid PRIMARY KEY,
  event_id uuid NOT NULL REFERENCES events(id),
  buyer_id uuid NOT NULL,
  idempotency_key varchar(100) NOT NULL,
  request_fingerprint varchar(64) NOT NULL,
  seat_codes_csv text NOT NULL,
  expires_at_utc timestamptz NOT NULL,
  seat_count integer NOT NULL CHECK (seat_count BETWEEN 1 AND 6),
  status integer NOT NULL DEFAULT 0 CHECK (status IN (0,1)),
  UNIQUE(buyer_id, idempotency_key)
);
CREATE TABLE orders (
  id uuid PRIMARY KEY,
  hold_id uuid NOT NULL UNIQUE REFERENCES holds(id),
  buyer_id uuid NOT NULL,
  idempotency_key varchar(100) NOT NULL,
  created_at_utc timestamptz NOT NULL,
  UNIQUE(buyer_id, idempotency_key)
);
INSERT INTO events(id,name,starts_at_utc) VALUES
('11111111-1111-1111-1111-111111111111', 'Demo Concert', '2027-04-20T18:00:00Z');
INSERT INTO seats(event_id, seat_code)
SELECT '11111111-1111-1111-1111-111111111111', 'A-' || lpad(i::text, 3, '0')
FROM generate_series(1,100) AS s(i);

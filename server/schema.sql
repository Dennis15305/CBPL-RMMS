PRAGMA journal_mode = WAL;
PRAGMA synchronous = FULL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS schema_info (
    version INTEGER NOT NULL
);

INSERT INTO schema_info(version)
SELECT 2
WHERE NOT EXISTS (SELECT 1 FROM schema_info);

UPDATE schema_info SET version = 2;

CREATE TABLE IF NOT EXISTS roll_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    plc_event_id INTEGER NOT NULL UNIQUE,
    plc_timestamp_utc INTEGER NOT NULL,
    plc_timestamp_valid INTEGER NOT NULL CHECK (plc_timestamp_valid IN (0, 1)),
    unwind_number INTEGER NOT NULL CHECK (unwind_number BETWEEN 1 AND 5),
    shift_number INTEGER NOT NULL CHECK (shift_number BETWEEN 0 AND 2),
    end_reason INTEGER NOT NULL CHECK (end_reason IN (1, 2)),
    length_mm INTEGER NOT NULL CHECK (length_mm BETWEEN 0 AND 4294967295),
    received_at_utc TEXT NOT NULL,
    raw_packet_hex TEXT NOT NULL,
    plc_acknowledged_at_utc TEXT,
    central_sync_state TEXT NOT NULL DEFAULT 'pending'
        CHECK (central_sync_state IN ('pending', 'syncing', 'synced', 'error')),
    central_attempts INTEGER NOT NULL DEFAULT 0,
    central_last_attempt_at_utc TEXT,
    central_synced_at_utc TEXT,
    central_last_error TEXT
);

CREATE INDEX IF NOT EXISTS ix_roll_events_central_sync
    ON roll_events(central_sync_state, plc_event_id);

CREATE INDEX IF NOT EXISTS ix_roll_events_plc_time
    ON roll_events(plc_timestamp_utc);

CREATE TABLE IF NOT EXISTS collector_meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);

-- Migration from the first development build: a non-zero RTC value can still
-- be an obviously invalid legacy date (for example 2006 after RTC reset).
UPDATE roll_events
SET plc_timestamp_valid = CASE
    WHEN plc_timestamp_utc >= 1577836800 AND plc_timestamp_utc < 4102444800 THEN 1
    ELSE 0
END;

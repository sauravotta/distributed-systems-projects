CREATE TABLE tickets (
    id UUID PRIMARY KEY,
    idempotency_key VARCHAR(128) NOT NULL UNIQUE,
    request_hash VARCHAR(64) NOT NULL,
    requester_email VARCHAR(254) NOT NULL,
    subject VARCHAR(200) NOT NULL,
    description VARCHAR(4000) NOT NULL,
    category VARCHAR(32) NOT NULL,
    assigned_team VARCHAR(100) NOT NULL,
    sla_hours INTEGER NOT NULL CHECK (sla_hours > 0),
    created_at TIMESTAMPTZ NOT NULL,
    notification_status VARCHAR(16) NOT NULL
        CHECK (notification_status IN ('pending', 'sent', 'failed'))
);

CREATE INDEX ix_tickets_created_at ON tickets (created_at DESC);

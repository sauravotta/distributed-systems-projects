CREATE TABLE notifications (
    id UUID PRIMARY KEY,
    ticket_id UUID NOT NULL,
    idempotency_key VARCHAR(160) NOT NULL UNIQUE,
    recipient VARCHAR(254) NOT NULL,
    subject VARCHAR(200) NOT NULL,
    body VARCHAR(4000) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);

#!/usr/bin/env python3
import json
import time
import uuid
import urllib.error
import urllib.request

TICKET_API = "http://127.0.0.1:8080"
ROUTING_API = "http://127.0.0.1:8081"
NOTIFICATION_API = "http://127.0.0.1:8082"
RUN_ID = f"smoke-{uuid.uuid4().hex[:10]}"


def request(method, url, payload=None, headers=None, timeout=12):
    body = None if payload is None else json.dumps(payload).encode()
    request_headers = {"Content-Type": "application/json"}
    if headers:
        request_headers.update(headers)
    req = urllib.request.Request(url, data=body, headers=request_headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        return error.code, json.loads(error.read())


def set_fault(service, mode):
    status, _ = request("POST", f"{service}/__faults/{mode}")
    assert status == 200, f"Could not set {service} fault mode to {mode}: {status}"


def wait_for_services():
    deadline = time.monotonic() + 90
    while time.monotonic() < deadline:
        try:
            statuses = [
                request("GET", f"{TICKET_API}/health/ready", timeout=2)[0],
                request("GET", f"{ROUTING_API}/health/ready", timeout=2)[0],
                request("GET", f"{NOTIFICATION_API}/health/ready", timeout=2)[0],
            ]
            if statuses == [200, 200, 200]:
                return
        except (OSError, ValueError):
            pass
        time.sleep(2)
    raise AssertionError("Services did not become ready within 90 seconds")


def create_ticket(key, subject="VPN access request"):
    return request(
        "POST",
        f"{TICKET_API}/api/tickets",
        {
            "requesterEmail": "alex@example.com",
            "subject": subject,
            "description": "Unable to connect to the company VPN.",
            "category": "network",
        },
        {"Idempotency-Key": f"{RUN_ID}-{key}"},
    )


def main():
    wait_for_services()
    set_fault(ROUTING_API, "normal")
    set_fault(NOTIFICATION_API, "normal")

    set_fault(ROUTING_API, "transient")
    status, created = create_ticket("smoke-transient-0001")
    assert status == 201 and created["notificationStatus"] == "sent", (status, created)

    status, duplicate = create_ticket("smoke-transient-0001")
    assert status == 200 and duplicate["id"] == created["id"], (status, duplicate)

    status, conflict = create_ticket("smoke-transient-0001", "Different subject")
    assert status == 409 and conflict["error"] == "idempotency_key_reused", (status, conflict)

    set_fault(ROUTING_API, "slow")
    started = time.monotonic()
    status, timeout_failure = create_ticket("smoke-routing-slow-0001")
    elapsed = time.monotonic() - started
    assert status == 503 and timeout_failure["error"] == "dependency_unavailable", (status, timeout_failure)
    assert 4 < elapsed < 10, f"Expected bounded client timeouts and retries, got {elapsed:.2f}s"

    set_fault(ROUTING_API, "normal")
    status, _ = create_ticket("smoke-after-timeout-0001")
    assert status == 201, f"Dependency did not recover after the timeout scenario: {status}"

    set_fault(ROUTING_API, "unavailable")
    status, failure = create_ticket("smoke-routing-down-0001")
    assert status == 503 and failure["error"] == "dependency_unavailable", (status, failure)
    assert "no ticket was created" in failure["message"]

    set_fault(ROUTING_API, "normal")
    set_fault(NOTIFICATION_API, "unavailable")
    status, degraded = create_ticket("smoke-notify-down-0001")
    assert status == 201 and degraded["notificationStatus"] == "failed", (status, degraded)
    status, stored = request("GET", f"{TICKET_API}/api/tickets/{degraded['id']}")
    assert status == 200 and stored["notificationStatus"] == "failed", (status, stored)

    set_fault(NOTIFICATION_API, "normal")
    status, resent = request(
        "POST",
        f"{TICKET_API}/api/tickets/{degraded['id']}/notification-retry",
    )
    assert status == 200 and resent["notificationStatus"] == "sent", (status, resent)

    notification_ticket_id = str(uuid.uuid4())
    notification_payload = {
        "ticketId": notification_ticket_id,
        "recipient": "alex@example.com",
        "subject": "Idempotency check",
        "body": "Repeated notification requests must not create duplicates.",
    }
    notification_key = f"ticket:{notification_ticket_id}:created"
    status, notification = request(
        "POST",
        f"{NOTIFICATION_API}/api/notifications",
        notification_payload,
        {"Idempotency-Key": notification_key},
    )
    assert status == 202, (status, notification)
    duplicate_status, duplicate_notification = request(
        "POST",
        f"{NOTIFICATION_API}/api/notifications",
        notification_payload,
        {"Idempotency-Key": notification_key},
    )
    assert duplicate_status == 202 and duplicate_notification["id"] == notification["id"], (
        duplicate_status,
        duplicate_notification,
    )

    set_fault(ROUTING_API, "transient")
    status, recovered = create_ticket("smoke-recovery-0001")
    assert status == 201, (status, recovered)

    set_fault(ROUTING_API, "unavailable")
    for index in range(3):
        status, _ = create_ticket(f"smoke-breaker-{index:04d}")
        assert status == 503

    started = time.monotonic()
    status, open_circuit = create_ticket("smoke-breaker-open-0001")
    elapsed = time.monotonic() - started
    assert status == 503, (status, open_circuit)
    assert elapsed < 0.5, f"Circuit breaker did not fail fast ({elapsed:.2f}s)"

    set_fault(ROUTING_API, "normal")
    set_fault(NOTIFICATION_API, "normal")
    print("Smoke checks passed: retries, idempotency, timeout bounds, explicit routing failure, notification degradation/recovery, circuit break.")


if __name__ == "__main__":
    main()

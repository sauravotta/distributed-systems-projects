# Service Desk Workflow

A local-first, production-oriented **.NET 10** proof of concept that shows how a service stays predictable when its dependencies are slow, flaky, or down.

The domain is an IT service desk: users submit support tickets, a **Routing** service assigns a team and SLA, and a **Notifications** service tells the requester. All service-to-service calls are **synchronous HTTP**. There is no message broker, stream processor, or event-driven workflow.

## Contents

- [Architecture](#architecture)
- [What it demonstrates](#what-it-demonstrates)
- [Prerequisites](#prerequisites)
- [Run locally](#run-locally)
- [Try it](#try-it)
- [Resilience demos](#resilience-demos)
- [Tests](#tests)
- [Observability](#observability)
- [Configuration](#configuration)
- [API reference](#api-reference)
- [Project layout](#project-layout)
- [Design decisions and limits](#design-decisions-and-limits)
- [Troubleshooting](#troubleshooting)

## Architecture

```
                         +--------------------+
  client ---HTTP---->    |     Ticket API     |  :8080   DB: tickets
                         | (idempotency, SLA) |
                         +----+----------+----+
        required, retried     |          |     best-effort, retried
       (failure => 503)       v          v     (failure => status "failed")
                  +-----------------+  +----------------------+
                  | Routing service |  | Notification service |
                  |      :8081      |  |   :8082  DB: notifications
                  +-----------------+  +----------------------+

  All services --OTLP--> OpenTelemetry Collector --> Jaeger (traces) :16686
                                                 --> Prometheus (metrics) :9090
```

| Component | Role | State |
|---|---|---|
| `TicketApi` | Public API. Validates, deduplicates, calls dependencies, persists tickets. | PostgreSQL database `tickets` |
| `RoutingService` | Deterministic category -> team/SLA assignment. Stateless, safe to retry. | none |
| `NotificationService` | Records notifications idempotently (does not send real email). | PostgreSQL database `notifications` |
| `ServiceDesk.Contracts` | Shared request/response DTOs, validation rules, fault-injection controller. | n/a |

Each stateful service owns its own database; no service queries another service's data.

## What it demonstrates

| Concern | Behavior |
|---|---|
| Required vs. best-effort dependencies | Routing is required: if it is unavailable the API returns `503` and **no ticket is created**. Notifications are best-effort: the ticket is kept and `notificationStatus` is `failed`. |
| Timeouts | Every outbound call has a 2 s client timeout. |
| Bounded retries | Up to 3 attempts per call with linear backoff (100 ms, 200 ms) for timeouts, network errors, `408`, `429`, and `5xx`. Other `4xx` are not retried. |
| Circuit breaker | Per dependency, in-memory. Opens after 3 consecutive failed calls and fails fast for 15 s, then allows a single half-open probe. |
| Idempotency | `POST /api/tickets` requires an `Idempotency-Key`. Same key and payload returns the original ticket (`200`); same key with a different payload returns `409`. Routing and Notifications also accept stable idempotency keys. |
| Partial-failure recovery | `POST /api/tickets/{id}/notification-retry` re-sends a failed notification safely. |
| Fault injection | Development-only endpoints put a service into `normal`, `transient`, `slow`, or `unavailable` mode. |
| Observability | Structured JSON logs, distributed traces, runtime/HTTP metrics via OpenTelemetry. |
| Schema management | Versioned SQL migrations (in `db/migrations/`) applied at startup under an advisory lock. |

## Prerequisites

| Tool | Version | Needed for |
|---|---|---|
| Docker Desktop (or Docker Engine) with Compose v2 | 20.10+ | Running the full stack |
| Python | 3.8+ | `tests/smoke.py` (standard library only) |
| .NET SDK | 10.0 | Running unit tests / building outside Docker (optional) |
| `curl` | any | Manual API calls |

Free local ports: `8080`, `8081`, `8082`, `9090`, `16686`, `8889`. All ports bind to `127.0.0.1` only.

## Run locally

All commands are run from this directory (`service-desk-workflow/`).

1. **Start everything** (builds the three service images and pulls PostgreSQL, the OTel Collector, Jaeger, and Prometheus on first run):

   ```sh
   docker compose up --build -d
   ```

2. **Wait for readiness.** Each should return `{"status":"ready"}`:

   ```sh
   curl -s http://127.0.0.1:8080/health/ready
   curl -s http://127.0.0.1:8081/health/ready
   curl -s http://127.0.0.1:8082/health/ready
   ```

   Check container state with `docker compose ps`; PostgreSQL should show `healthy`.

3. **Follow logs** (optional):

   ```sh
   docker compose logs -f ticket-api routing-service notification-service
   ```

4. **Verify end to end** with the automated smoke test (see [Tests](#tests)):

   ```sh
   python3 tests/smoke.py
   ```

5. **Stop:**

   ```sh
   docker compose down        # keep the PostgreSQL volume (data persists)
   docker compose down -v     # also delete stored tickets/notifications
   ```

| URL | What |
|---|---|
| http://127.0.0.1:8080 | Ticket API |
| http://127.0.0.1:8081 | Routing service |
| http://127.0.0.1:8082 | Notification service |
| http://127.0.0.1:16686 | Jaeger UI (traces) |
| http://127.0.0.1:9090 | Prometheus UI (metrics) |

### Running a service without Docker (optional)

You can run the .NET services on the host while using Compose only for PostgreSQL and telemetry. Publish a Postgres port first (e.g. add `ports: ["127.0.0.1:5432:5432"]` to the `postgres` service), then in separate terminals:

```sh
export ASPNETCORE_ENVIRONMENT=Development
export OTEL_SDK_DISABLED=true   # skip OTLP export if the collector is not reachable

ASPNETCORE_URLS=http://127.0.0.1:8081 dotnet run --project src/RoutingService

ConnectionStrings__Notifications="Host=127.0.0.1;Database=notifications;Username=servicedesk;Password=local-only-password" \
ASPNETCORE_URLS=http://127.0.0.1:8082 dotnet run --project src/NotificationService

ConnectionStrings__Tickets="Host=127.0.0.1;Database=tickets;Username=servicedesk;Password=local-only-password" \
Services__Routing=http://127.0.0.1:8081 Services__Notifications=http://127.0.0.1:8082 \
ASPNETCORE_URLS=http://127.0.0.1:8080 dotnet run --project src/TicketApi
```

## Try it

Create a ticket (the `Idempotency-Key` header is required, 8-128 characters):

```sh
curl -i http://127.0.0.1:8080/api/tickets \
  -H 'Content-Type: application/json' \
  -H 'Idempotency-Key: demo-ticket-0001' \
  -d '{
    "requesterEmail": "alex@example.com",
    "subject": "VPN access request",
    "description": "Unable to connect to the company VPN.",
    "category": "network"
  }'
```

Expected: `201 Created` with the assigned team, SLA, and `notificationStatus: "sent"`:

```json
{
  "id": "…",
  "status": "created",
  "assignedTeam": "Network Operations",
  "slaHours": 4,
  "notificationStatus": "sent"
}
```

- **Repeat the exact request** -> `200 OK` with the same ticket (no duplicate).
- **Reuse the key with a different body** -> `409 Conflict`.
- **Fetch a ticket:** `curl http://127.0.0.1:8080/api/tickets/{id}`.

Routing rules:

| Category | Team | SLA (hours) |
|---|---|---|
| `account` | Service Desk | 8 |
| `hardware` | End User Computing | 24 |
| `software` | Application Support | 12 |
| `network` | Network Operations | 4 |
| `other` | Service Desk | 24 |

## Resilience demos

Fault endpoints exist on the Routing and Notification services and are only mapped when `ASPNETCORE_ENVIRONMENT=Development` (the Compose default for this demo).

```sh
curl -X POST http://127.0.0.1:8081/__faults/{mode}   # routing
curl -X POST http://127.0.0.1:8082/__faults/{mode}   # notifications
curl http://127.0.0.1:8081/__faults                  # show current mode
```

| Mode | Effect |
|---|---|
| `normal` | Healthy. |
| `transient` | The next 2 requests return `503`, then it recovers. |
| `slow` | Each request is delayed 5 s (exceeds the 2 s client timeout). |
| `unavailable` | Every request returns `503`. |

Set `normal` on both services when you are done.

### 1. Transient failures are absorbed by retries

```sh
curl -X POST http://127.0.0.1:8081/__faults/transient
# create a ticket with a NEW Idempotency-Key -> 201 (succeeds on the 3rd attempt)
```

Ticket API logs show two `retrying after ...` warnings.

### 2. Routing outage: explicit failure, no ticket

```sh
curl -X POST http://127.0.0.1:8081/__faults/unavailable
# create a ticket -> 503 {"error":"dependency_unavailable", ... "no ticket was created"}
```

### 3. Circuit breaker fails fast

With routing `unavailable`, send 3 ticket requests (each fails after retries). The 4th is rejected immediately without calling routing. Logs show `Circuit breaker for routing opened for 15 seconds`. After 15 s one probe request is allowed; set routing to `normal` and a request closes the circuit.

### 4. Slow dependency is bounded

```sh
curl -X POST http://127.0.0.1:8081/__faults/slow
# create a ticket -> 503 after roughly 6-7 s (3 attempts x 2 s timeout + backoff), not 15 s+
```

### 5. Notification outage degrades, ticket survives

```sh
curl -X POST http://127.0.0.1:8082/__faults/unavailable
# create a ticket -> 201 with "notificationStatus":"failed"
curl -X POST http://127.0.0.1:8082/__faults/normal
curl -X POST http://127.0.0.1:8080/api/tickets/{id}/notification-retry   # -> 200, "sent"
```

## Tests

**Unit tests** (validation rules and fault controller; no Docker needed):

```sh
dotnet test ServiceDeskWorkflow.slnx
```

**End-to-end smoke test** (requires the stack to be running). It exercises transient retries, idempotency and conflict, slow-dependency timeout bounds, routing outage, notification degradation and retry, duplicate notification idempotency, and circuit-breaker fail-fast, then resets all fault modes:

```sh
python3 tests/smoke.py
```

A passing run prints: `Smoke checks passed: retries, idempotency, timeout bounds, ...`. The test generates a unique idempotency prefix per run, so it is safe to re-run against persisted data. Allow about 30 s because it includes real timeouts and a breaker window.

## Observability

- **Traces:** open Jaeger at http://127.0.0.1:16686 and pick `ticket-api`; a ticket request shows the outbound calls to `routing-service` and `notification-service` with propagated trace context.
- **Metrics:** Services export ASP.NET Core, HTTP client, and .NET runtime metrics over OTLP to the Collector, which exposes them at http://127.0.0.1:8889/metrics (namespace `service_desk_`). Prometheus scrapes that endpoint; query it at http://127.0.0.1:9090 (for example `service_desk_aspnetcore_routing_match_attempts_total`).
- **Logs:** JSON console logs. Resilience events include retry warnings, circuit open/reject events, and dependency failures: `docker compose logs ticket-api`.

## Configuration

| Setting | Default (Compose) | Description |
|---|---|---|
| `ConnectionStrings__Tickets` | `Host=postgres;...Database=tickets` | Ticket API database |
| `ConnectionStrings__Notifications` | `Host=postgres;...Database=notifications` | Notification database |
| `Services__Routing` | `http://routing-service:8080` | Routing base URL |
| `Services__Notifications` | `http://notification-service:8080` | Notifications base URL |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://otel-collector:4317` | OTLP collector endpoint |
| `ASPNETCORE_ENVIRONMENT` | `Development` | Enables fault endpoints. **Do not use outside local demos.** |

Retry count, timeout, and breaker thresholds are constants in [`DependencyInvoker.cs`](src/TicketApi/DependencyInvoker.cs) and the HTTP client setup in [`Program.cs`](src/TicketApi/Program.cs).

## API reference

| Method | Path | Service | Description |
|---|---|---|---|
| `POST` | `/api/tickets` | Ticket | Create an idempotent ticket. `201` new, `200` replay, `400` invalid, `409` key reuse, `502/503` routing failure. |
| `GET` | `/api/tickets/{id}` | Ticket | Fetch a ticket and its notification status. |
| `POST` | `/api/tickets/{id}/notification-retry` | Ticket | Retry a failed/pending notification. |
| `POST` | `/api/routing/assignments` | Routing | Internal: assign team and SLA. |
| `POST` | `/api/notifications` | Notification | Internal: record an idempotent notification (`202`). |
| `GET` | `/health/live`, `/health/ready` | All | Liveness and readiness (Ticket/Notification check the database). |
| `GET/POST` | `/__faults`, `/__faults/{mode}` | Routing, Notification | Development-only fault injection. |

## Project layout

```
service-desk-workflow/
├── compose.yaml                 # Full local stack
├── Dockerfile                   # Shared multi-stage build (PROJECT build arg)
├── db/
│   ├── init/                    # Creates the notifications database
│   └── migrations/              # Versioned SQL, embedded and applied at startup
├── observability/               # OTel Collector and Prometheus config
├── src/
│   ├── ServiceDesk.Contracts/   # DTOs, validation, fault controller
│   ├── TicketApi/               # API, store, resilient dependency invoker
│   ├── RoutingService/
│   └── NotificationService/
└── tests/
    ├── ServiceDesk.Tests/       # xUnit tests
    └── smoke.py                 # End-to-end resilience checks
```

## Design decisions and limits

- **No distributed transaction.** The ticket is committed first, then the notification is attempted. The partial-success case is made explicit via `notificationStatus` plus a retry endpoint, rather than hidden.
- **Retries only where safe.** Routing is a pure lookup and notifications are keyed by a stable idempotency key backed by a unique constraint, so repeating them cannot duplicate side effects.
- **Process-local state.** The circuit breaker and fault mode live in memory and reset on restart; with multiple replicas each keeps its own view.
- **Not a production template.** Out of scope: authentication/authorization, secret management, TLS, rate limiting, real email delivery, cloud/Kubernetes deployment, and durable notification scheduling. Compose credentials are local demo values.
- **Fault endpoints are unauthenticated** and must never be exposed outside local development.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Port already in use | Stop the conflicting process or change the host port mapping in `compose.yaml`. |
| `ticket-api` restarts on first boot | It waits for PostgreSQL health; give it a few seconds and re-check `docker compose ps`. |
| Smoke test says services not ready | Wait up to 90 s for the first build/pull, then re-run. |
| Ticket creation returns `503` unexpectedly | A fault mode or open circuit may be active: reset with `__faults/normal` and wait 15 s for the breaker window. |
| Need a clean slate | `docker compose down -v && docker compose up --build -d`. |
| Notifications DB missing | `db/init` only runs on a new volume; use `docker compose down -v` and restart. |

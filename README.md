# PayFlow

**A simulated payment processing and merchant management platform**, built to demonstrate
professional engineering for payment systems: transaction state machines, idempotency,
double-entry ledgering, refunds, signed webhooks, settlement and reconciliation.

> ⚠️ **Educational simulation.** PayFlow does not connect to any bank, card network or acquirer,
> does not move real money, and rejects anything resembling real card data. All payment
> outcomes are produced by a deterministic simulator driven by test tokens such as `sim_card_success`.

## Status

| Phase | State |
|---|---|
| 1. Requirements & architecture | ☑ approved |
| 2. Backend foundation, database, authentication | ☑ tested |
| 3. Merchants & API credentials | ☑ tested |
| 4. Payments, lifecycle, idempotency | ☑ tested |
| 5. Refunds & reversals | ☑ tested |
| 6. Webhooks & delivery retries | ☑ tested (see [TESTING.md](docs/TESTING.md)) |
| 7. Settlement & reconciliation | ☑ tested |
| 8. Frontend & reporting | ☑ tested |
| 9. Hardening & automated testing | next |

Details and exit gates: [docs/ROADMAP.md](docs/ROADMAP.md).

## Stack

ASP.NET Core (.NET 10 LTS) · EF Core 10 · PostgreSQL 17 · Redis 7.4 · Angular 21 + Angular Material + ECharts ·
xUnit v3 + Testcontainers · Docker Compose · GitHub Actions

## Quick start (Docker)

Requires Docker Desktop (or Docker Engine) with Compose.

```bash
cp .env.example .env
```

Edit `.env` and replace every `change-me` value, then:

```bash
docker compose up --build
```

- Dashboard: <https://localhost:8443>. Sign in as the PlatformAdmin below. The certificate comes from Caddy's local CA, so browsers warn until you trust it, and curl needs `-k` (or `--cacert` with the root certificate from `docker compose cp gateway:/data/caddy/pki/authorities/local/root.crt .`).
- The API is on the same origin: `/api` (dashboard), `/v1` (merchant API), `/health/ready`.
- The dashboard API (`/api`) only works over HTTPS; plain <http://localhost:8080> is for health checks and debugging.
- If ports 5432, 6379, 8080 or 8443 are already in use, set `PAYFLOW_DB_PORT`, `PAYFLOW_REDIS_PORT`, `PAYFLOW_API_PORT` and `PAYFLOW_HTTPS_PORT` in `.env`.
- `API_KEY_PEPPER` must be a base64 secret of at least 32 bytes, and `PAYFLOW_MASTER_KEY` exactly 32 bytes (`openssl rand -base64 32` for both).
- Webhook sink: <http://localhost:8090> shows received events. Register `http://webhook-sink:8080/webhooks` as an endpoint, `POST /config` with `{"secret":"whsec_…"}` so it can verify signatures, or `{"failNext":2}` to watch retries.
- A PlatformAdmin is created on first start from `PAYFLOW_ADMIN_EMAIL` / `PAYFLOW_ADMIN_PASSWORD`.
- The `migrate` job applies migrations as `payflow_migrator`; the API runs as the restricted `payflow_app` role.

In the dashboard, a PlatformAdmin sees **Merchants**, **Settlements**, **Reconciliation** and **Reports**; merchant users see their **Overview**, **Transactions** (with lifecycle, ledger and refunds), **Settlements**, **Reports** and, for MerchantAdmins, **Webhooks**.

The same flow through the API (full reference in [API.md](docs/API.md)):
1. `GET /api/auth/antiforgery`, then `POST /api/auth/login` as the PlatformAdmin, with `X-XSRF-TOKEN` set from the `XSRF-TOKEN` cookie.
2. `POST /api/merchants` with an `adminEmail`; note the one-time `invitationToken`.
3. `POST /api/merchants/{id}/status` with `{"status":"Active"}`.
4. The invitee calls `POST /api/auth/accept-invitation`, logs in, and creates a key with `POST /api/merchants/{id}/api-keys`.
5. `GET /v1/merchant` with `Authorization: Bearer pf_test_sk_…`.
6. `POST /v1/payments` with an `Idempotency-Key` header and `{"amount":"100.00","currency":"ZAR","paymentMethod":"sim_card_success","merchantReference":"order-1"}`. Retry it with the same key to see the replay.
7. `GET /v1/payments/{id}` for the lifecycle, or `GET /api/merchants/{id}/payments/{paymentId}` on the dashboard for timings and ledger entries.
8. `POST /api/merchants/{id}/webhook-endpoints` with the sink URL, then watch `GET /api/merchants/{id}/webhook-deliveries` and the sink page.

## Developing without Docker for the API

Run Postgres and Redis from Compose, and the API with `dotnet run`:

```bash
docker compose up -d postgres redis
```

```bash
dotnet user-secrets --project src/PayFlow.Api set "ConnectionStrings:PayFlow" "Host=localhost;Database=payflow;Username=payflow_migrator;Password=<from .env>"
```

```bash
dotnet user-secrets --project src/PayFlow.Api set "ConnectionStrings:Redis" "localhost:6379,password=<from .env>"
```

```bash
dotnet run --project src/PayFlow.Api
```

In Development the API applies migrations on start, which is why it connects as the migrator role locally.

## Tests

```bash
dotnet test --solution PayFlow.slnx
```

Integration tests need Docker. See [docs/TESTING.md](docs/TESTING.md), including a Windows long-path workaround.

Dashboard (Node 22.14+):

```bash
cd web/payflow-dashboard
npm ci --ignore-scripts
npm run test:ci          # Vitest: components, money formatting, axe accessibility
npm run start:gateway    # http://localhost:4200 against the running Compose stack (see proxy.conf.mjs)
```

## Repository layout

```
src/PayFlow.Api            host: middleware, auth wiring, health, startup
src/PayFlow.SharedKernel   Money, currencies, public ids, results, redaction
src/PayFlow.Infrastructure DbContext, outbox, correlation, HTTP helpers
src/Modules/*              one project per module (Identity, Audit, Merchants, Ledger, Payments, Refunds, Webhooks, Settlement, Reconciliation, Reporting)
src/PayFlow.Webhooks.Signature  dependency-free signing/verification library for receivers
src/PayFlow.Migrations     EF Core migrations
tests/*                    unit, architecture, integration tests
deploy/postgres            database role setup
deploy/caddy               local HTTPS gateway
web/payflow-dashboard      Angular dashboard (served by nginx behind the gateway)
tools/webhook-sink         demo webhook receiver
docs/                      design documentation
```

## Documentation

| Document | Contents |
|---|---|
| [REQUIREMENTS.md](docs/REQUIREMENTS.md) | Scope, roles, requirements with traceable IDs |
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Modular monolith, boundaries, technology, security, ADRs |
| [DATABASE.md](docs/DATABASE.md) | Domain model, schemas, constraints, ledger, DB roles |
| [PAYMENT_LIFECYCLE.md](docs/PAYMENT_LIFECYCLE.md) | State machines, money/fee/refund/settlement rules, idempotency, sequence diagrams |
| [TESTING.md](docs/TESTING.md) | Test suites, how to run them, exit-gate coverage |
| [ROADMAP.md](docs/ROADMAP.md) | Phases, deliverables, exit gates |
| [API.md](docs/API.md) | Endpoints, conventions, error codes |
| [SETTLEMENT.md](docs/SETTLEMENT.md) | Settlement batches, carry-forward, payouts, concurrency guarantees |
| [RECONCILIATION.md](docs/RECONCILIATION.md) | Statement format, generator, matching rules, exception workflow, adjustments |
| SECURITY.md · DEPLOYMENT.md | Written as the corresponding phases are built |

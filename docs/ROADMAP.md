# PayFlow — Implementation Roadmap

Each phase ends with an **exit gate**: its listed tests pass in CI and its docs are updated.
A module is never marked done before its tests pass. Status values: ☐ not started · ◐ in progress · ☑ done (tested).

| Phase | Scope | Key deliverables | Exit gate (must pass) | Status |
|---|---|---|---|---|
| **1. Requirements & architecture** | Scope, architecture, domain model, financial rules | REQUIREMENTS, ARCHITECTURE, DATABASE, PAYMENT_LIFECYCLE, ROADMAP | Design reviewed and approved by project owner | ☑ approved 2026-11-12 |
| **2. Foundation, DB, auth** | Solution skeleton, SharedKernel (`Money`, IDs, `Result`), `PayFlowDbContext`, migrations, Docker Compose (Postgres, Redis), correlation id, ProblemDetails, Serilog redaction, Identity + cookie auth, roles, tenant context, audit module, outbox table, CI pipeline | Running API + `/health/ready`; seeded PlatformAdmin | `Money` unit tests (all currencies, rounding, cross-currency guard); architecture tests (module boundaries, no float money); login/lockout/CSRF integration tests; append-only trigger tests | ☑ 2026-11-12: all tests pass; Compose stack verified end to end (migrate job, restricted DB role, HTTPS gateway, admin login) |
| **3. Merchants & API keys** | Merchant CRUD + status machine, fee plans (versioned), currencies/limits, memberships, API key create/rotate/revoke, API-key auth handler, Redis key cache, rate limiting | `/api/merchants…`, `/api/merchants/{id}/api-keys…` | Key shown once & never logged (log capture test); revoked/expired key → 401; rotation grace period; cross-tenant access → 403 for another merchant's routes, 404 for another merchant's resources; rate limit → 429 | ☑ 2026-11-12: 84 unit, 9 architecture, 42 integration tests pass; reviewer walkthrough on Compose passes |
| **4. Payments, lifecycle, idempotency** | `Payment` aggregate + transition whitelist, simulator, fee calc, ledger module (PaymentCaptured/Fee), idempotency filter, pending-resolution + recovery workers, PAN detector | `POST/GET /v1/payments` | Exhaustive state-transition table test; fee examples A–F; **50-parallel idempotency test**; mismatched payload → 422; ledger balances per entry; crash-recovery test (Processing → resolved) | ☑ 2026-11-12: 212 unit, 9 architecture, 67 integration tests pass; payments walkthrough on Compose passes |
| **5. Refunds & reversals** | Refund + reversal aggregates, reservation, operator limits, refund processor worker, ledger postings | `POST /v1/payments/{id}/refunds`, `/reversal`, `GET /v1/refunds/{id}` | **20-parallel refund oversubscription test**; DB check constraint test (bypassing domain); reversal eligibility matrix; no row ever deleted | ☑ 2026-11-13: 234 unit, 9 architecture, 81 integration tests pass; refunds walkthrough on Compose passes |
| **6. Webhooks** | Endpoint config + encrypted secrets, outbox dispatcher, event fan-out, signed sender, retry/back-off, attempts log, manual redelivery, SSRF guard, `tools/webhook-sink` | `/api/webhooks/…`, sink app | Signature verification vector tests; replay window; retries with fake time; exactly-once event creation under 2 dispatchers; SSRF blocklist tests | ☑ 2026-11-13: 293 unit, 9 architecture, 96 integration tests pass; webhook walkthrough with the sink on Compose passes |
| **7. Settlement & reconciliation** | Settlement runs (advisory lock, carry-forward), payout simulation, CSV export; statement import (hash dedupe), statement generator with injected discrepancies, matching engine, exception workflow, adjustments | `/api/settlements…`, `/api/reconciliation…`; SETTLEMENT and RECONCILIATION docs | Worked example reproduces ZAR 313.26; double-run → one batch; deficit carry-forward; every reconciliation outcome type produced from a generated statement; rerun idempotent | ☐ |
| **8. Frontend & reporting** | Angular shell, auth, role-aware nav; merchant overview, transaction explorer + detail (timeline, ledger, refunds, webhooks), refund dialog, settlement dashboard, reconciliation workspace, webhook history, reports (ECharts); reporting queries + Redis cache | Dashboard served by nginx | Component unit tests (Vitest); empty/loading/error states covered; money formatting tests per currency; axe accessibility checks | ☐ |
| **9. Hardening & automated testing** | Security review (ASVS L2 checklist), load test (k6) on payments, chaos-style worker restarts, full Playwright E2E for the 11 reviewer scenarios, coverage gates | TESTING.md, SECURITY.md finalised | All 11 definition-of-done scenarios green in E2E; no high findings open | ☐ |
| **10. Deployment & docs** | Production-style Compose (secrets, non-root images, migration job), GitHub Actions release, README walkthrough, API.md, DEPLOYMENT.md, final diagrams | Tagged release | Fresh clone → `docker compose up` → seeded demo works end to end on a clean machine | ☐ |

## Decisions confirmed at Phase 1 approval

These are defaulted in the design; change any of them now and the docs will be updated.

1. **Default demo currency ZAR**, with USD/EUR/GBP/JPY/KWD also enabled.
2. **Refunds keep the fee** (merchant bears it); reversals return it.
3. **Single-step sale** only (no authorise/capture).
4. **Cookie auth** for the dashboard via same-origin nginx (not JWT in the browser).
5. **Angular 21**, final pin after checking the installed Angular CLI in Phase 2.
6. Settlement **T+1** (cut-off = start of current UTC day), triggered manually by a PlatformAdmin with an optional daily schedule.

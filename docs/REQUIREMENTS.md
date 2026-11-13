# PayFlow — Requirements and Scope (Phase 1)

> **Simulation notice.** PayFlow is an educational simulation of a payment provider's
> internal systems. It never connects to card networks, banks or acquirers, never moves
> real money, and never accepts or stores card numbers, CVVs or bank credentials.
> Every "payment method" is a clearly fake simulation token (for example `sim_card_success`).

## 1. Goals

1. Demonstrate production-grade engineering for payment systems: explicit state machines,
   idempotency, double-entry ledger, settlement, reconciliation and signed webhooks.
2. Treat financial data integrity as the primary non-functional requirement.
3. Deliver as a single deployable **modular monolith** (ASP.NET Core + Angular + PostgreSQL).
4. Be reviewable: a reviewer can run `docker compose up`, seed demo data and walk through
   all 11 "definition of done" scenarios, and run the automated test suite.

## 2. Out of scope (deliberately)

| Excluded | Reason |
|---|---|
| Real card / bank / acquirer integration | Simulation only. |
| PAN, CVV, expiry, IBAN, account numbers | Never accepted; requests containing a Luhn-valid 13–19 digit number are rejected. |
| Authorise-then-capture (two-step) payments | Single-step "sale" keeps scope manageable. Documented as a future extension. |
| Foreign exchange / multi-currency settlement | Each payment settles in its own currency. No FX conversion. |
| Chargebacks / disputes | Future extension; the ledger model accommodates it. |
| Microservices, message brokers (Kafka/RabbitMQ) | A transactional outbox + DB-polled workers is sufficient and simpler. |
| Multi-region / HA deployment | Single-node Docker Compose deployment. |

## 3. Actors and roles

| Role | Surface | Scope | Key permissions |
|---|---|---|---|
| **Platform Administrator** | Dashboard | All merchants | Onboard/suspend merchants, view all transactions, configure fee plans, investigate failures, run settlement, import statements, resolve reconciliation exceptions, view audit log. |
| **Merchant Administrator** | Dashboard | Own merchant | Edit profile, create/rotate/revoke API keys, manage webhook endpoints, manage merchant users, view transactions, reports, settlements, initiate refunds/reversals. |
| **Merchant Operator** | Dashboard | Own merchant | View/search transactions, initiate refunds within configured limit, view settlement reports. No credential or webhook management. |
| **Merchant system** (machine) | Merchant API (`/v1`) | Own merchant | Create payments, query payments, create refunds/reversals, list events. Authenticated by API key. |

Tenant isolation is mandatory: a merchant-scoped principal can never read or affect another merchant's data, and this is covered by automated tests.

## 4. Functional requirements

IDs are referenced from tests and the roadmap.

### A. Merchant management (MER)
- **MER-1** Platform admin registers a merchant (legal name, trading name, contact email, country, enabled currencies, fee plan). Merchant starts `PendingActivation`.
- **MER-2** Merchant status lifecycle: `PendingActivation → Active ⇄ Suspended → Closed`. Only `Active` merchants may create payments. Suspended merchants may still be refunded/settled.
- **MER-3** Merchant admin edits profile fields (not legal name or status).
- **MER-4** API keys: generate, list (prefix + metadata only), rotate (old key valid for a configurable grace period, default 24 h), revoke immediately. The full secret is shown **once** at creation.
- **MER-5** Merchant users: merchant admin invites users and assigns `MerchantAdmin` or `MerchantOperator`.
- **MER-6** Merchant-specific permissions: per-merchant refund limit for operators, enabled currencies, max single payment amount.
- **MER-7** Activity history: every merchant-affecting action is visible in an audit timeline.

### B. Payment initiation (PAY)
- **PAY-1** `POST /v1/payments` with amount, currency, simulated payment method token, merchant order reference, optional description and metadata. `Idempotency-Key` header is **required**.
- **PAY-2** Validation: amount > 0, scale ≤ currency minor units, currency enabled for merchant, amount within merchant/currency limits, metadata size limits, no PAN-like data.
- **PAY-3** Every payment gets a unique, immutable public reference (`pay_…`).
- **PAY-4** Outcome is simulated by the payment method token (`sim_card_success`, `sim_card_declined_insufficient_funds`, `sim_card_declined_do_not_honour`, `sim_card_processor_error`, `sim_card_pending`, `sim_card_pending_then_declined`, …). Pending payments resolve asynchronously via a background simulator.
- **PAY-5** API responses and UI label all transactions as simulated.

### C. Transaction lifecycle (TXN)
- **TXN-1** Explicit state machine with a whitelist of transitions (see PAYMENT_LIFECYCLE.md). Any other transition is rejected and logged.
- **TXN-2** Capture id, merchant id, amount, currency, status, created/updated UTC timestamps, idempotency key, failure code and reason.
- **TXN-3** Every state change is appended to an immutable status history.
- **TXN-4** Platform admins can search across merchants and filter by failure code.

### D. Idempotency (IDEM)
- **IDEM-1** Same merchant + same key + same payload → original response replayed, no new financial operation.
- **IDEM-2** Same key + different payload → `422 idempotency_key_reused`.
- **IDEM-3** Same key while the original is still in flight → `409 idempotency_request_in_progress` (retry later).
- **IDEM-4** Applies to: create payment, create refund, create reversal, (dashboard) settlement run and statement import.
- **IDEM-5** Proven by a concurrency test firing ≥ 50 parallel identical requests.

### E. Refunds and reversals (REF)
- **REF-1** Full and partial refunds of `Succeeded`/`PartiallyRefunded` payments.
- **REF-2** Sum of succeeded + pending refunds can never exceed the captured amount (domain rule **and** DB check constraint).
- **REF-3** Refund history per payment; each refund is its own record linked to the original payment.
- **REF-4** Refund states: `Pending → Succeeded | Failed`.
- **REF-5** Reversal: full cancellation of a `Succeeded` payment that has no refunds and is not yet settled. Reversals also return the processing fee.
- **REF-6** Original payments are never deleted or overwritten; refunds/reversals are new records + new ledger entries.

### F. Webhooks (WH)
- **WH-1** Merchant admin configures endpoints (HTTPS URL, subscribed event types, enabled flag). Signing secret shown once; can be rolled.
- **WH-2** Events: `payment.succeeded`, `payment.declined`, `payment.failed`, `payment.pending`, `refund.succeeded`, `refund.failed`, `payment.reversed`, `settlement.paid`.
- **WH-3** Each event has a unique `evt_…` id; each delivery is HMAC-SHA256 signed with a timestamp.
- **WH-4** Retries with exponential back-off + jitter, max 8 attempts over ~24 h, then `Exhausted`. Manual redelivery.
- **WH-5** Every attempt is recorded: request headers (redacted), response status, truncated body, latency, error.
- **WH-6** Duplicate handling: events are created exactly once per domain change (outbox); receivers dedupe on event id. A reference receiver is provided.
- **WH-7** SSRF protection on endpoint URLs.

### G. Settlement (SET)
- **SET-1** Settlement run per merchant per currency for all eligible items up to a cut-off.
- **SET-2** Net = gross payments − fees − refunds + carried-forward balance (negative net pays 0 and carries the deficit forward). Reversed payments are excluded because reversal is only allowed before settlement.
- **SET-3** Batch states: `Pending → Paid | Failed`; failed batches can be retried, never re-composed.
- **SET-4** A payment/refund can belong to at most one settlement batch (unique constraint).
- **SET-5** Settlement history and per-batch line-item report (CSV export).

### H. Reconciliation (REC)
- **REC-1** Import a simulated external statement (CSV). Duplicate file imports are rejected by content hash.
- **REC-2** A generator produces realistic statements from internal data with optional injected discrepancies (for demos and tests).
- **REC-3** Matching engine classifies: Matched, MissingExternal, MissingInternal, AmountMismatch, CurrencyMismatch, DuplicateExternal, StatusDiscrepancy.
- **REC-4** Exception workflow: `Open → InReview → Resolved | Dismissed`, with mandatory note, actor and resolution type.
- **REC-5** Reruns are deterministic and do not duplicate exceptions.

### I. Reporting (RPT)
- **RPT-1** Transaction volume (count, value) by day, merchant, currency.
- **RPT-2** Success / decline / failure rates and top failure codes.
- **RPT-3** Refund totals and refund rate.
- **RPT-4** Settlement summaries.
- **RPT-5** Reconciliation exceptions by type and age.
- **RPT-6** Processing performance: p50/p95 processing latency, webhook delivery success rate and latency.

## 5. Non-functional requirements

| Area | Requirement |
|---|---|
| Integrity | `decimal`/`numeric` money only; ledger balances to zero for every journal entry; DB constraints back every critical rule. |
| Auditability | Append-only ledger, status history and audit log; UTC `timestamptz` everywhere. |
| Security | See ARCHITECTURE.md §7. OWASP ASVS L2 as the reference checklist. |
| Performance (target, single node) | `POST /v1/payments` p95 < 150 ms excluding simulated delay; 100 req/s sustained. |
| Reliability | Webhook and settlement workers survive restarts without loss or duplication (outbox + `SKIP LOCKED`). |
| Observability | Structured JSON logs, correlation IDs, health checks, OpenTelemetry traces/metrics. |
| Testability | Every module has unit tests; financial rules have DB-backed integration tests (Testcontainers). |
| Accessibility | Dashboard targets WCAG 2.2 AA. |

## 6. Definition-of-done traceability

| # | Reviewer scenario | Requirements |
|---|---|---|
| 1 | Register and manage a merchant | MER-1..7 |
| 2 | Initiate a simulated payment | PAY-1..5 |
| 3 | View the transaction lifecycle | TXN-1..4 |
| 4 | Retry without duplicates | IDEM-1..5 |
| 5 | Partial refund | REF-1..4 |
| 6 | Webhook config + delivery attempts | WH-1..7 |
| 7 | Generate a settlement | SET-1..5 |
| 8 | Import a statement | REC-1..2 |
| 9 | Identify discrepancies | REC-3..5 |
| 10 | Financial / operational reports | RPT-1..6 |
| 11 | Run automated tests | TESTING.md (Phase 9) |

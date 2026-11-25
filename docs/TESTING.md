# PayFlow — Testing

Status: **Phases 2–9 covered**: 497 backend tests (unit, architecture, integration) and 99 dashboard tests, all passing, with coverage gates in CI. Phase 9 added the Playwright suite for the 11 reviewer scenarios, a chaos test that kills the API under load, a k6 load test, OpenTelemetry tests, session-lifetime tests and multi-factor sign-in tests (`TotpTests` with the RFC 6238 vectors, `MfaTests`, dashboard specs) (2026-11-24).

## Test projects

| Project | What it proves | Needs Docker |
|---|---|---|
| `tests/PayFlow.UnitTests` | Money rules (precision, half-up rounding, currency mismatch, the documented fee and settlement examples), public ids, redaction in JSON and logs, correlation-id validation, merchant-access authorization, merchant status whitelist (every pair), fee plan safety rules, API key format, HMAC verification and states | No |
| `tests/PayFlow.ArchitectureTests` | Module boundaries, layer dependencies, no floating-point types anywhere, every `decimal` column is `numeric(19,4)`, every module mapping is registered for migrations, no tables in `public` | No |
| `tests/PayFlow.IntegrationTests` | The real API against PostgreSQL 17 and Redis 7.4 containers (Testcontainers), connected as the restricted `payflow_app` role exactly as in production | Yes |
| `web/payflow-dashboard` (`*.spec.ts`) | Angular components in jsdom with a mocked HTTP backend (Vitest), money formatting per currency, page states, refund idempotency, axe-core accessibility rules | No |

## Running

```bash
dotnet test --solution PayFlow.slnx
```

Single project:

```bash
dotnet test --project tests/PayFlow.IntegrationTests
```

Integration tests need a running Docker engine. On Windows, `dotnet test` can fail with
*"The filename or extension is too long"* when the repository sits in a very deep folder (the
260-character path limit). Clone to a short path such as `C:\src\PayFlow`, or run the built test
executable directly:

```bash
dotnet tests/PayFlow.IntegrationTests/bin/Debug/net10.0/PayFlow.IntegrationTests.dll
```

## How the integration host works

`PayFlowTestHost` (an xUnit v3 assembly fixture) starts the containers once per run, then:

1. applies [`deploy/postgres/sql/roles.sql`](../deploy/postgres/sql/roles.sql), the same file Docker Compose uses;
2. runs EF migrations as the schema owner;
3. starts the API with `WebApplicationFactory`, connected as `payflow_app`.

Webhook tests share one class (so they run one after another) because the dispatcher and sender work on the shared database; each sends to its own unique host on a `StubWebhookReceiver` and only inspects that host. Tests that use the recovery or refund jobs assert on their own records, never on how many items a job handled, because those jobs also see other tests' data.

Tests use unique users and identifiers rather than cleaning tables, because audit tables are
append-only by design and cannot be truncated. **Never fail a login against the shared seeded
admin**: five failures lock it for every test that follows. Wrong-password tests create their own users.

The factory registers a `CapturingLogSink`, so tests can assert that secrets never reach the logs.

## Phase 2 exit gate: covered

| Gate item | Tests |
|---|---|
| `Money` (all currencies, rounding, cross-currency guard) | `MoneyTests` |
| Architecture (module boundaries, no float money) | `ArchitectureRules` |
| Login / lockout / CSRF | `AuthenticationTests`: CSRF rejection, cookie flags, identical failure responses, lock-out after 5 failures, logout, validation, strict JSON, rate limiting, audit with correlation id |
| Append-only triggers | `DatabaseIntegrityTests`: UPDATE/DELETE/TRUNCATE rejected even for the owner; app role lacks UPDATE/DELETE and DDL |
| Platform | `PlatformTests`: health, correlation ids, security headers, problem details, every `/api` endpoint declares authorization, currency seed |

## Phase 3 exit gate: covered

| Gate item | Tests |
|---|---|
| Key shown once and never logged | `ApiKeyTests`: only a 32-byte HMAC is stored; the secret is absent from listings, audit rows, other columns and every captured log line |
| Revoked/expired key → 401 | Revocation is effective immediately despite the Redis cache; closing a merchant disables its keys; forged and malformed keys are rejected |
| Rotation grace period | The old key works during the grace period; with no grace period it stops at once; a key can't be rotated twice |
| Cross-tenant access | Another merchant's routes → 403; another merchant's key through your own route → 404; operators can't manage keys; a dashboard cookie is not accepted on `/v1` |
| Rate limit → 429 | Per-key quota with `Retry-After`; a second API instance sees the same Redis counter |
| Merchant management | Onboarding with invitation, status lifecycle, currency precision rules, immutable versioned fee plans, single-use invitations, last-admin protection, concurrent edits (no 500s) |

## Phase 4 exit gate: covered

| Gate item | Tests |
|---|---|
| Exhaustive state-transition table | `PaymentDomainTests`: all 100 status pairs checked against the documented diagram, written out independently of the code |
| Fee examples A–F | Calculator reproduces A, B, C, E, F; D (below minimum) is rejected by merchant limits; a fee can never reach the amount |
| 50-parallel idempotency | `PaymentApiTests`: 50 identical concurrent requests → one payment, one ledger capture; the others replay it or get `409` with `Retry-After` |
| Mismatched payload → 422 | Same key, different amount → `idempotency_key_reused` |
| Ledger balances per entry | After 50 concurrent distinct payments the merchant balance is exactly 50 × net, and no entry is unbalanced; the DB rejects an unbalanced entry at commit and any edit to postings |
| Crash recovery | `PaymentWorkerTests`: transaction 1 only, then `409` while stuck; recovery after the timeout completes it once; the retry replays `Succeeded` |
| Also | Pending → succeeded/declined/expired with a fake clock; card numbers refused in any field and never logged; keys released after pre-execution rejections; suspended merchants refused; tenant isolation; cursor pagination; over-refund blocked by a CHECK constraint |

## Phase 5 exit gate: covered

| Gate item | Tests |
|---|---|
| 20-parallel refund oversubscription | `RefundTests`: 20 concurrent ZAR 10.00 refunds on ZAR 100.00 → exactly 10 accepted, 10 `refund_exceeds_refundable_balance`; reserved total exactly 100.00; after processing, 10 ledger refund entries and status `Refunded` |
| DB check constraint (bypassing the domain) | `PaymentApiTests`: a direct `UPDATE` setting refunded above the amount violates `ck_payments_refund_balance` |
| Reversal eligibility matrix | `RefundDomainTests`: clean / settled / partially refunded / refund pending / fully refunded / declined / already reversed; integration: reversal returns the fee and zeroes the merchant's net; a refund and a reversal racing on one payment never both succeed (5 rounds) |
| No row ever deleted | `DELETE` on payments, refunds and reversals and `UPDATE` on reversals fail even for the schema owner; the app role lacks `DELETE` |
| Also | Partial then full refund with the documented lifecycle and ledger (merchant ends at −4.90: refunds keep the fee); failed refunds release their reservation; idempotent refunds under 20-way concurrency; operator limits per currency; PlatformAdmins cannot move merchant money; tenant isolation; refund audit trail |

## Phase 6 exit gate: covered

| Gate item | Tests |
|---|---|
| Signature verification vectors | `WebhookSignatureTests`: two vectors computed independently with Python's `hmac`; tampered body, timestamp or secret fail; dual-secret headers verify with either secret; malformed headers are rejected |
| Replay window | Timestamps more than 5 minutes old, or ahead, are rejected |
| Retries with fake time | `WebhookTests`: 500 → 503 → 200 on the documented schedule (first retry 48–72 s); identical body every attempt; 8 failures → `Exhausted`, no ninth automatic attempt; manual redelivery succeeds as attempt 9 |
| Exactly-once event creation under 2 dispatchers | 10 payments, two dispatchers in parallel → exactly 10 events and 10 deliveries; every outbox message has exactly one event |
| SSRF blocklist | `SsrfGuardTests`: 19-address table (private, loopback, link-local/metadata, CGNAT, multicast, IPv6 ULA/link-local, IPv4-mapped, NAT64); URL policy; the real handler refuses `localhost`/`127.0.0.1` at connect time; integration: unsafe URLs → `400 webhook_url_not_allowed` |
| Also | Secrets shown once and stored as AES-GCM ciphertext (`SecretProtectorTests`: purpose binding, tamper and wrong-key detection); key ring encrypted; signature values redacted in attempt logs; disabled or unsubscribed endpoints get nothing; tenant isolation for events and deliveries; regression test for the payment completion race |

## Phase 7 exit gate: covered

| Gate item | Tests |
|---|---|
| Worked example reproduces ZAR 313.26 | `SettlementComposeTests` (domain arithmetic); `SettlementTests`: payments A, B, C and a 40.00 refund through the API → batch net and payout 313.26, merchant payable 313.26 before payout and 0 after, one `SettlementPaid` entry, one `settlement.paid` outbox message, CSV lines sum to 313.26 |
| Double run → one batch | 8 parallel runs for one merchant → exactly 1 batch holding all 5 payments; the others skip (`open_batch_exists` / `concurrent_settlement`); a run after payment creates nothing |
| Deficit carry-forward | Settled payment refunded in full → next batch nets −100.00, pays 0, carries −100.00; the following batch nets 150.00 − 6.35 − 100.00 = 43.65; merchant payable ends at 0 |
| Every reconciliation outcome from a generated statement | `ReconciliationTests`: one of each injection → exactly 4 Matched and 1 of each of the six discrepancy outcomes, 6 exceptions, each found on the record it was planted on; a faithful statement (including a reversal and a USD payment, with a decline that must not appear) matches everything |
| Rerun idempotent | Second run → `exceptionsCreated: 0`, `exceptionsSeenAgain: 6`, identical counts, every exception `timesSeen: 2`; closed exceptions stay closed; still one adjustment entry |
| Also | Matching precedence, duplicates in file order, status mapping table (14 cases), fingerprint stability, amount formatting (`ReconciliationEngineTests`); CSV parsing rejects every bad line at once; duplicate file import → 409; malformed import stores nothing; review → resolve with a `ReconciliationAdjustment` that moves `acquirer_receivable` by exactly the amount; dismissal needs a note; a failed payout posts nothing and is retried once; a settled payment cannot be reversed; tenant isolation and admin-only routes; append-only triggers on the new tables |

Integration tests that need to cross a settlement cut-off run their own host on a `FakeTimeProvider`. Reconciliation tests put their payments on a day of their own, years ahead, because a statement covers every merchant in its period and the database is shared.

Test payment references contain no digits (`PaymentScenario.UniqueReference`). Random hex occasionally formed a 13-digit, Luhn-valid run, which the card-number guard correctly rejected, making webhook tests fail now and then.

## Phase 8 exit gate: covered

| Gate item | Tests |
|---|---|
| Component unit tests (Vitest) | `PaymentsPage` (rows, filters, inclusive UTC dates, paging params), `RefundDialog`, `LoginPage`, `ReportsPage`, `StateView`, `navFor` (role-aware navigation), `loadable` (stale responses ignored) |
| Empty / loading / error states | `StateView` renders exactly one of the four states, with the correlation id and a working retry; the transactions page is tested in all three non-ready states against the mocked API |
| Money formatting per currency | `money.spec.ts`: ZAR, USD, EUR, GBP (2 decimals), JPY (0), KWD (3); database scale dropped but never rounded; negatives; values beyond 2^53 stay exact; amount validation (precision, positive, above refundable) on BigInt minor units; rates |
| axe accessibility checks | axe-core (WCAG 2.0–2.2 A/AA rules) finds no violations on the sign-in page, the transactions table, the refund dialog and the reports page. jsdom cannot measure colour contrast, so that rule is off in tests; the palette in `styles.scss` was chosen for 4.5:1 |
| Also | Refunds reuse one `Idempotency-Key` across retries (a network failure then a retry sends the same key); API refusals become plain explanations; sign-in shows one message for every credential failure; reporting backend: `ReportingQueryTests` (defaults, limits, rates, cache keys) and `ReportingTests` (exact figures for a known merchant, 60-second cache, per-currency separation, tenant isolation, platform-only reconciliation backlog) |

The dashboard was also driven in the browser against the Compose stack (sign-in, overview, transaction explorer, detail with ledger, refund dialog rejecting an over-refund then refunding, reports with charts).

## End-to-end tests (Playwright)

`tests/e2e` drives the reviewer scenarios (REQUIREMENTS.md §6) through the dashboard and the merchant API against the running Compose stack. The browser uses the Angular dev server on `http://localhost:4200`, whose proxy verifies the HTTPS gateway against Caddy's local CA, so no certificate check is ever skipped.

```bash
docker compose up -d
docker compose cp gateway:/data/caddy/pki/authorities/local/root.crt web/payflow-dashboard/.certs/caddy-root.crt
cd tests/e2e && npm ci && npx playwright install chromium
npm test
```

The platform admin comes from `PAYFLOW_E2E_ADMIN_EMAIL` / `PAYFLOW_E2E_ADMIN_PASSWORD` / `PAYFLOW_E2E_ADMIN_TOTP_SECRET`, or locally from `.env` (`PAYFLOW_ADMIN_TOTP_SECRET`: platform admins sign in with an authenticator code, which the helpers compute; a code is accepted once, so back-to-back admin sign-ins wait up to 30 s for a fresh one). k6 reads the same secret. Every run creates its own merchants and users. Scenarios reuse one session per user, because PayFlow limits sign-in to 10 attempts a minute per client and that limit is not relaxed for tests; if a run still hits it, the helper waits out the window once.

Scenario 6 talks to the reference receiver (`tools/webhook-sink`) on its loopback port (`PAYFLOW_SINK_PORT` from `.env`, or `PAYFLOW_E2E_SINK_URL`). It sets the sink's signing secret to the new endpoint's, so deliveries to the sink for other demo endpoints fail verification until the sink is restarted (`docker compose restart webhook-sink`).

**Do not run the load test on the stack you use for the scenarios on the same day.** Scenario 8 generates the acquirer statement for the whole platform for today; after a k6 run (about 100,000 payments) that statement has ~30,000 lines, and pasting 3 MB of CSV into the dashboard's import box stalls the browser (the API itself imports it in 7–18 s). On 2026-11-24 this blocked scenarios 8–10 after a load test; they passed on 2026-11-23. Use a separate stack for load tests, or run the scenarios on another day. A streamed file import is a follow-up.

A full run takes about 3 minutes, mostly real waiting: PayFlow's first webhook retry (1 minute ± 20 %), the settlement cut-off (minute precision, never in the future) and the 60-second report cache. Nothing in the stack is sped up or faked for the tests.

| Scenario | What the suite does | Status |
|---|---|---|
| 1. Register and manage a merchant | UI: register with a first admin, activate, accept the invitation (token only in the `#` fragment), first sign-in, issue an API key shown once, use it | ✅ |
| 2–3. Simulated payment and its lifecycle | API payment; UI explorer filter, timeline, ledger lines | ✅ |
| 4. Retry without duplicates | Byte-identical replay with `Idempotent-Replayed`, changed body → 422 `idempotency_key_reused`, one row in the UI | ✅ |
| 5. Partial refund | UI dialog rejects an over-refund, refund completes, remaining balance 60.00 | ✅ |
| 6. Webhook config and delivery attempts | UI: URL with credentials refused; endpoint for one event, secret shown once; first delivery answered 500 → Retrying with next attempt; PayFlow's own retry succeeds (attempt 2, HTTP 200, body excerpt); the sink verifies every signature; redelivery reuses the `evt_` id and the sink flags the duplicate; secret roll with grace period, ping verified with the new secret; disable | ✅ |
| 7. Generate a settlement | 3 payments, 1 decline, a partial refund; admin runs settlement for the merchant at a cut-off, simulated bank rejection, retry → Paid; second run creates nothing; merchant sees gross − fees − refunds = net = payout, 4 line items, CSV export with exactly those items | ✅ |
| 8. Import a statement | Generate today's statement with one of each discrepancy planted, import it; the same file again is rejected by content hash; a malformed file is rejected with errors | ✅ |
| 9. Identify discrepancies | Matching classifies all six planted kinds (6 new exceptions); amount mismatch Open → In review → Resolved (note required), missing internal dismissed; decision recorded with actor and note; rerun raises 0 new, 6 seen again, closed ones stay closed | ✅ |
| 10. Financial and operational reports | Merchant report exact for that merchant: 4 payments, ZAR 399.99 succeeded, 75.00 % success, top failure code, ZAR 20.00 refunded (5.00 %), 1 paid batch, payout equals the batch; platform report adds the reconciliation backlog and webhook figures | ✅ |
| 11. Run automated tests | This suite plus `dotnet test` and `npm run test:ci` (sections above) | ✅ |

What the browser suite does not repeat, because the unit and integration tests cover it more precisely: the SSRF guard's private, loopback and link-local rules (`SsrfGuardTests`, `WebhookTests`; the Compose demo allows private networks so it can reach the sink), retry exhaustion after 8 attempts (`WebhookUnitTests`), carry-forward of a negative net (`SettlementComposeTests`), and concurrent settlement runs (`SettlementTests`).

## Chaos: worker restarts

`tests/e2e/chaos/worker-restarts.spec.ts` checks the reliability requirement (REQUIREMENTS.md §5: workers survive restarts without loss or duplication). It SIGKILLs the `api` container, which also runs every background worker, four times: twice while 40 payments arrive (success, decline, pending-then-success, pending-then-decline), and twice while 10 partial refunds are processed, pending payments fall due and webhooks are delivered to the reference receiver. The client retries with the same Idempotency-Key, as a merchant's server would.

```bash
cd tests/e2e && npm run test:chaos     # kills and restarts the Compose api container
```

After the stack converges, the test asserts exactly the state a calm run would leave:

- one payment per key, each with its simulated outcome;
- each refund completed once;
- exactly one event per state change (no event lost or duplicated by a replayed outbox message);
- one delivery per event, each received by the sink with a valid signature;
- every journal entry balanced, and the merchant owed exactly 32 × 95.10 − 10 × 5.00.

It also requires that the crashes actually interrupted requests. Runs in about 2.5 minutes; the 2026-11-24 run passed.

## Load test (k6)

`tests/load/payments.js` checks the performance target (REQUIREMENTS.md §5: `POST /v1/payments` p95 < 150 ms, 100 req/s sustained) through the TLS gateway, with TLS verified against Caddy's CA.

```bash
docker compose --profile load run --rm k6                        # 20 s warm-up, then 100 req/s for 2 min
docker compose --profile load run --rm -e RATE=50 -e DURATION=30s k6
```

Setup creates two merchants with ten API keys each through the dashboard API (per-key quota 600 a minute, left on). Every tenth payment is sent twice with the same key and must come back as a replay of the same payment.

Thresholds, all on the sustained phase:

- p95 < 150 ms;
- errors < 1 %;
- checks > 99 %;
- almost no dropped iterations;
- at least 95 % of the target request count, so an empty run cannot pass.

Result on the reference machine (Docker Desktop on Windows, 12 vCPUs, other stacks running), 2026-11-24:

| | Before Phase 9 fixes | After |
|---|---|---|
| Throughput achieved | ~35 req/s (7,791 iterations dropped) | 100 req/s (none dropped) |
| p95 / p99 | 16.3 s / 20.1 s | 73 ms / 150 ms |
| Failed requests | 13.9 % (`53300 too many clients`) | 0 of 13,677 |

The fixes (SECURITY.md, SEC-5):

- an asynchronous log sink: the synchronous console sink stalled request threads while transactions were open;
- a process-wide cache of committed ledger account ids, saving six round trips per payment;
- the connection pool capped at 32, below Postgres's 100 connections.

Later the same day, after the MFA work, the same test failed on the same laptop: p95 0.4–1.4 s at 80–100 req/s, and up to 9 s on cold caches. Evidence points to the machine rather than the code:

- low-load latency is unchanged (median 47 ms at 2 req/s);
- turning tracing off made no difference;
- during the run the host was at 93 % CPU with the processor at 75 % of its base clock (a 15 W laptop chip, throttled after a day of builds and tests);
- nothing in the MFA change touches `POST /v1/payments`.

The database had also grown to 112,000 payments with Postgres's default 128 MB `shared_buffers`. **Re-run the load test on a cool machine (or a dedicated box) before relying on the figures above.**

With OpenTelemetry export on and 10 % trace sampling, p95 is 105 ms. Full sampling with export costs about 0.5 s at this rate (ARCHITECTURE.md §8).

## Coverage gates

Both test suites fail CI when coverage drops below a minimum set just under the measured figures. Raise the minimums as coverage improves.

| Suite | Measured (2026-11-24) | Minimum enforced | Where |
|---|---|---|---|
| Backend: unit + architecture + integration, migrations and tests excluded | 92.3 % lines, 80.6 % branches | 90 % lines, 78 % branches | `ci.yml` (ReportGenerator merge + `build/check-coverage.py`) |
| Dashboard (Vitest, v8) | 77.8 % statements, 73.0 % branches, 58.0 % functions, 77.0 % lines | 74 / 69 / 51 / 72 | `angular.json` `coverageThresholds` |

Locally:

```bash
dotnet test --solution PayFlow.slnx -c Release --coverage --coverage-output-format cobertura --results-directory TestResults/coverage
dotnet reportgenerator -reports:"TestResults/coverage/*.cobertura.xml" -targetdir:coverage-report -reporttypes:"Cobertura;TextSummary" -assemblyfilters:"+PayFlow.*;-PayFlow.Migrations;-PayFlow.*Tests"
python build/check-coverage.py coverage-report/Cobertura.xml --line 90 --branch 78
cd web/payflow-dashboard && npm run test:ci      # coverage on, thresholds enforced
```

The dashboard tests run on at most four Vitest workers (`vitest.config.ts`): one jsdom + Material + axe worker per core oversubscribed a busy machine, and under coverage instrumentation single axe scans then exceeded a minute.

## End-to-end check against Docker Compose

Besides the automated suites, each phase is checked on the real stack (`docker compose up --build`)
through the HTTPS gateway: admin login, merchant onboarding, invitation, API key creation, a `/v1`
call, the plain-HTTP rejection, and a scan of the API logs and audit table for the issued secret.

## CI

`.github/workflows/ci.yml` builds with warnings as errors, checks that migrations match the
model, runs all tests with coverage (GitHub's Ubuntu runners provide Docker for Testcontainers),
enforces the coverage minimums, fails on vulnerable packages, and builds the API and dashboard
images. The Playwright, chaos and k6 suites need the full Compose stack and run locally (sections
above).

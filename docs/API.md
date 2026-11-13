# PayFlow — API Reference

Status: **covers Phases 2–6** (authentication, merchants, API keys, members, payments, refunds, webhooks). Settlement and reconciliation endpoints are added as their phases are built.

> Every payment in PayFlow is simulated. No request ever reaches a bank or card network.

## Conventions

| Topic | Rule |
|---|---|
| Base URL (local) | `https://localhost:8443` via the Compose gateway |
| Content | JSON (`application/json`). **Unknown properties are rejected** with 400. |
| Money | Decimal **strings** with the currency's exact minor units, e.g. `"100.00"` (ZAR), `"1000"` (JPY), `"12.345"` (KWD). Over-precise amounts are rejected, never rounded. |
| Identifiers | Prefixed public ids: `mer_`, `usr_`, `key_`, `fp_` + 26 characters. |
| Time | ISO 8601, UTC (`2026-11-12T10:00:00+00:00`). |
| Errors | RFC 9457 problem details with a stable `code` and `correlationId` (see below). |
| Correlation | Send `X-Correlation-Id` (8–64 of `A-Z a-z 0-9 . _ : -`) or receive a generated one; it is echoed on every response and stored on audit records. |
| Caching | Every response is `Cache-Control: no-store`. |

### Error shape

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.1",
  "title": "The request is invalid.",
  "status": 400,
  "detail": "One or more fields are invalid.",
  "instance": "/api/merchants",
  "code": "validation_failed",
  "errors": { "contactEmail": ["'Contact Email' is not a valid email address."] },
  "correlationId": "0192f3c8a1b27c3d9e0f..."
}
```

| HTTP | Typical codes |
|---|---|
| 400 | `validation_failed`, `invalid_amount`, `unsupported_currency`, `reason_required`, `https_required`, `invalid_invitation`, `malformed_request` |
| 401 | `authentication_required`, `invalid_credentials`, `api_key_required`, `invalid_api_key` |
| 403 | `forbidden`, `csrf_token_invalid` |
| 404 | `merchant_not_found`, `api_key_not_found`, `member_not_found` |
| 409 | `member_exists`, `api_key_limit_reached`, `concurrency_conflict` |
| 422 | `invalid_status_transition`, `merchant_closed`, `currency_not_in_fee_plan`, `last_merchant_admin`, `api_key_not_rotatable` |
| 429 | `rate_limited` (with `Retry-After`) |

## Two API surfaces

| Surface | Prefix | Authentication | CSRF | Rate limit |
|---|---|---|---|---|
| Dashboard | `/api` | Session cookie `__Host-payflow.session` (HTTPS only) | Required on POST/PUT/PATCH/DELETE: echo the `XSRF-TOKEN` cookie in `X-XSRF-TOKEN` | Login: 10/min per IP |
| Merchant API | `/v1` | `Authorization: Bearer pf_test_sk_…` | Not applicable | 600/min per key, shared across instances (Redis). Responses carry `RateLimit-Limit` and `RateLimit-Remaining` |

The two never mix: a dashboard cookie is not accepted on `/v1`, and API keys are not accepted on `/api`.

## Dashboard: authentication

| Method & path | Who | Purpose |
|---|---|---|
| `GET /api/auth/antiforgery` | anyone | Sets the `XSRF-TOKEN` cookie. Call before the first POST. |
| `POST /api/auth/login` | anyone | `{ "email", "password" }` → current user. Wrong password, unknown user and locked account all return the same `401 invalid_credentials`. 5 failures lock the account for 15 minutes. Issues a fresh XSRF token. |
| `POST /api/auth/accept-invitation` | invited user | `{ "email", "token", "password" }` sets the first password (12+ characters). Tokens are single-use and expire after 3 days. |
| `GET /api/auth/me` | signed in | `{ id, email, displayName, isPlatformAdmin, memberships: [{ merchantId, role }] }` |
| `POST /api/auth/logout` | signed in | Ends the session. |

## Dashboard: merchants (Phase 3)

Roles: **PA** = PlatformAdmin, **MA** = MerchantAdmin of that merchant, **MM** = any member of that merchant. PlatformAdmins pass every merchant check.

| Method & path | Role | Purpose |
|---|---|---|
| `POST /api/merchants` | PA | Register a merchant (starts `PendingActivation`) and optionally invite its first MerchantAdmin. |
| `GET /api/merchants?status=&search=&page=&pageSize=` | PA | List and search merchants. |
| `GET /api/merchants/{merchantId}` | MM | Merchant profile, status, fee plan and currency limits. |
| `PATCH /api/merchants/{merchantId}` | MA | `{ tradingName, contactEmail }` |
| `POST /api/merchants/{merchantId}/status` | PA | `{ status: "Active" \| "Suspended" \| "Closed", reason }`. A reason is required to suspend or close. Closing disables all API keys immediately. |
| `PUT /api/merchants/{merchantId}/currencies/{currency}` | PA | `{ maxPaymentAmount, operatorRefundLimit }` enable or update a currency. |
| `PUT /api/merchants/{merchantId}/fee-plan` | PA | `{ feePlanId }` |
| `GET /api/merchants/{merchantId}/activity?before=&limit=` | MM | Audit trail for the merchant, newest first. Continue with `before = nextBefore`. |
| `GET /api/fee-plans` | PA | All fee plan versions. |
| `POST /api/fee-plans` | PA | `{ name, percentageRate: "0.029", terms: [{ currency, fixedFee, minPaymentAmount }] }` creates the next version of `name`. Versions are immutable. |

Register example:

```json
POST /api/merchants
{
  "legalName": "Demo Store (Pty) Ltd",
  "tradingName": "Demo Store",
  "contactEmail": "ops@demo.test",
  "countryCode": "ZA",
  "currencies": [{ "currency": "ZAR", "maxPaymentAmount": "25000.00", "operatorRefundLimit": "500.00" }],
  "adminEmail": "owner@demo.test",
  "adminDisplayName": "Demo Owner"
}
```

`201` returns `{ merchant: {...}, adminInvitation: { userId, email, role, invitationToken } }`. The token is shown **once**; pass it to the invitee out of band. PayFlow sends no e-mail.

## Dashboard: API keys and members (Phase 3)

| Method & path | Role | Purpose |
|---|---|---|
| `GET /api/merchants/{merchantId}/api-keys` | MA | Key metadata: id, name, prefix, state (`Active`, `Expiring`, `Expired`, `Revoked`), created/last used/expiry. Never the secret. |
| `POST /api/merchants/{merchantId}/api-keys` | MA | `{ name }` → `201 { key, secret, notice }`. **The secret is returned only here.** At most 10 usable keys per merchant. |
| `POST /api/merchants/{merchantId}/api-keys/{keyId}/rotate` | MA | `{ gracePeriodHours }` (0–168, default 24) → new key + secret; the old key keeps working until the grace period ends. |
| `POST /api/merchants/{merchantId}/api-keys/{keyId}/revoke` | MA | Revokes immediately (cache invalidated). |
| `GET /api/merchants/{merchantId}/members` | MA | Members, roles, and whether each invitation was accepted. |
| `POST /api/merchants/{merchantId}/members` | MA | `{ email, displayName, role: "MerchantAdmin" \| "MerchantOperator" }` → invitation (token only for users without a password). |
| `PATCH /api/merchants/{merchantId}/members/{userId}` | MA | `{ role }`. The last MerchantAdmin cannot be demoted. |
| `DELETE /api/merchants/{merchantId}/members/{userId}` | MA | Removes access (effective within one minute). The last MerchantAdmin cannot be removed. |

A merchant's resources are only reachable through that merchant's route: another merchant's key id under your own merchant returns `404`, and another merchant's route returns `403`.

## Merchant API (`/v1`)

API key format: `pf_test_sk_<8-char prefix>_<52-char secret>` (`test` because PayFlow only simulates).

| Method & path | Purpose |
|---|---|
| `GET /v1/merchant` | The authenticated merchant: `{ id, tradingName, status, currencies, simulated: true }`. Use it to verify a key. |

```bash
curl -k https://localhost:8443/v1/merchant -H "Authorization: Bearer pf_test_sk_..."
```

### Payments (Phase 4)

| Method & path | Purpose |
|---|---|
| `POST /v1/payments` | Create a simulated payment. **`Idempotency-Key` header required** (1–255 printable ASCII characters). |
| `GET /v1/payments/{paymentId}` | The payment with its full `statusHistory`. Another merchant's id returns `404`. |
| `GET /v1/payments?status=&limit=&startingAfter=` | Newest first, cursor pagination: pass `nextStartingAfter` from the previous page. `limit` ≤ 100. |

Request:

```json
POST /v1/payments
Idempotency-Key: order-1001-attempt
{
  "amount": "100.00",
  "currency": "ZAR",
  "paymentMethod": "sim_card_success",
  "merchantReference": "order-1001",
  "description": "Two coffees",
  "metadata": { "table": "4" }
}
```

`201 Created`:

```json
{
  "id": "pay_01m78n...",
  "object": "payment",
  "merchantReference": "order-1001",
  "amount": "100.00",
  "currency": "ZAR",
  "status": "Succeeded",
  "paymentMethod": "sim_card_success",
  "fee": "4.90",
  "net": "95.10",
  "refundedAmount": "0.00",
  "failureCode": null,
  "failureReason": null,
  "simulated": true,
  "createdAt": "2026-11-12T20:05:11.2+00:00",
  "completedAt": "2026-11-12T20:05:11.2+00:00"
}
```

**Simulation tokens** (`paymentMethod`). Anything else is `400`; card numbers anywhere in the request are `400 card_data_not_accepted`.

| Token | Outcome |
|---|---|
| `sim_card_success` | `Succeeded` |
| `sim_card_declined_insufficient_funds` | `Declined`, `insufficient_funds` |
| `sim_card_declined_do_not_honour` | `Declined`, `do_not_honour` |
| `sim_card_processor_error` | `Failed`, `processor_error` |
| `sim_card_pending` | `Pending`, then `Succeeded` after about 10 s |
| `sim_card_pending_then_declined` | `Pending`, then `Declined` after about 10 s |
| `sim_card_pending_expires` | `Pending`, then `Expired` after 30 min |
| `sim_card_refund_fails` | `Succeeded`; refunds against it fail (Phase 5) |

**Idempotency behaviour**

| Situation | Response |
|---|---|
| First request | `201`, processed once |
| Same key, same body (any key order or formatting) | Original response replayed byte for byte, `Idempotent-Replayed: true` |
| Same key, different body | `422 idempotency_key_reused` |
| Same key while the first is still processing | `409 idempotency_request_in_progress`, `Retry-After: 1` |
| First request was rejected before a payment existed (`400`, `422`) | Key released: fix the request and reuse it |
| Key older than 24 h whose payment exists | `409 duplicate_idempotency_key` |

A replay always returns the original response; a payment created as `Pending` replays as `Pending`. Use `GET` for the current state.

**Payment errors:** `400 invalid_amount`, `unsupported_currency`, `card_data_not_accepted`, `idempotency_key_required`, `idempotency_key_invalid`; `422 merchant_not_active`, `currency_not_enabled`, `amount_below_minimum`, `amount_above_maximum`.

### Refunds and reversals (Phase 5)

| Method & path | Purpose |
|---|---|
| `POST /v1/payments/{paymentId}/refunds` | `{ "amount": "40.00", "reason": "Item returned" }` → `201` refund in `Pending`. Omit `amount` to refund the whole remaining balance. **`Idempotency-Key` required.** |
| `POST /v1/payments/{paymentId}/reversal` | `{ "reason": "Customer cancelled" }` → `201` reversal. Full cancellation including the fee; only for a `Succeeded` payment with no refunds that is not yet settled. **`Idempotency-Key` required.** |
| `GET /v1/payments/{paymentId}/refunds` | Refund history `{ paymentId, refunds: [...], reversal }`. |
| `GET /v1/refunds/{refundId}` | One refund (`re_…`). |

Refunds are completed asynchronously (within a few seconds) and end `Succeeded` or `Failed`. A payment shows `refundedAmount` and `refundableAmount` (amount − refunded − pending), and moves to `PartiallyRefunded` or `Refunded` only when a refund succeeds. Refunds keep the original processing fee; reversals return it.

**Refund errors:** `400 invalid_amount`; `404 payment_not_found`; `422 payment_not_refundable`, `refund_exceeds_refundable_balance`, `payment_not_reversible`; `403 operator_refund_limit_exceeded`, `operator_refund_not_permitted`, `merchant_users_only` (dashboard). Idempotency behaves as for payments.

### Events (Phase 6)

| Method & path | Purpose |
|---|---|
| `GET /v1/events?type=&limit=&startingAfter=` | Events for the merchant, newest first (cursor pagination). |
| `GET /v1/events/{eventId}` | One event (`evt_…`) with its `data`. |

## Dashboard: webhooks (Phase 6)

| Method & path | Role | Purpose |
|---|---|---|
| `GET /api/merchants/{merchantId}/webhook-endpoints` | MA | Endpoints with the secret's last 4 characters only. |
| `POST /api/merchants/{merchantId}/webhook-endpoints` | MA | `{ url, description, eventTypes }` → `201 { endpoint, secret }`. The `whsec_…` secret is shown **once**. At most 5 endpoints. |
| `PATCH /api/merchants/{merchantId}/webhook-endpoints/{endpointId}` | MA | `{ url?, description?, eventTypes?, isEnabled? }`. Endpoints are disabled, never deleted. |
| `POST …/webhook-endpoints/{endpointId}/roll-secret` | MA | New secret (shown once). The old one keeps signing alongside it for 24 h. |
| `POST …/webhook-endpoints/{endpointId}/ping` | MA | Sends a `webhook.ping` event to that endpoint → `202` with the delivery. |
| `GET /api/merchants/{merchantId}/webhook-deliveries?status=&eventType=&endpointId=&page=` | MM | Delivery history. |
| `GET …/webhook-deliveries/{deliveryId}` | MM | One delivery with every attempt: status, duration, response excerpt (2 KB), error kind, request headers with the signature masked. |
| `POST …/webhook-deliveries/{deliveryId}/redeliver` | MA | Send again now, with a fresh 8-attempt budget. |

Event types: `payment.pending`, `payment.succeeded`, `payment.declined`, `payment.failed`, `payment.expired`, `payment.reversed`, `refund.succeeded`, `refund.failed`, `webhook.ping`, or `*` for all. URLs must be `https` to a public host: credentials, `localhost`, internal names and private, loopback or link-local addresses are refused (`400 webhook_url_not_allowed`), and the resolved address is checked again when sending. Signature format and retry schedule: [PAYMENT_LIFECYCLE.md §9.3](PAYMENT_LIFECYCLE.md).

## Dashboard: payments (Phase 4)

| Method & path | Role | Purpose |
|---|---|---|
| `GET /api/merchants/{merchantId}/payments?status=&failureCode=&reference=&from=&to=&page=&pageSize=` | MM | Transaction explorer for one merchant. |
| `GET /api/merchants/{merchantId}/payments/{paymentId}` | MM | Detail: payment, status history, idempotency key, fee plan and rate, processing time, ledger entries. |
| `GET /api/payments?merchantId=&status=&failureCode=&…` | PA | Search across all merchants, for example every `processor_error` failure. |
| `GET /api/merchants/{merchantId}/payments/{paymentId}/refunds` | MM | Refund history and reversal for a payment. |
| `POST /api/merchants/{merchantId}/payments/{paymentId}/refunds` | MA, MerchantOperator | Refund from the dashboard (`Idempotency-Key` header). Operators are limited to the merchant's per-currency operator limit. |
| `POST /api/merchants/{merchantId}/payments/{paymentId}/reversal` | MA | Reverse from the dashboard (`Idempotency-Key` header). |

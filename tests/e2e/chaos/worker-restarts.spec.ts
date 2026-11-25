import { expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { Dashboard, MerchantApi, WebhookSink, activeMerchant } from '../support/payflow';
import { compose, crashApi, interrupted, scalar, sql, untilAnswered } from '../support/chaos';

/**
 * Chaos: the API process (HTTP and every background worker) is SIGKILLed four times while payments
 * are being created, pending payments are waiting to resolve, refunds are being processed and
 * webhooks are being delivered. docs/REQUIREMENTS.md §5 (Reliability): workers survive restarts
 * without loss or duplication. Afterwards everything must converge to exactly the state a calm run
 * would produce: one payment per Idempotency-Key, the simulated outcome for each, every refund
 * completed once, one event per state change, each delivered with a valid signature, and a ledger
 * that balances to the cent.
 *
 *   npm run test:chaos      (kills and restarts the api container of the Compose stack)
 */
const PLAN = [
  { method: 'sim_card_success', count: 24, final: 'Succeeded', events: ['payment.succeeded'] },
  { method: 'sim_card_pending', count: 8, final: 'Succeeded', events: ['payment.pending', 'payment.succeeded'] },
  { method: 'sim_card_declined_do_not_honour', count: 4, final: 'Declined', events: ['payment.declined'] },
  { method: 'sim_card_pending_then_declined', count: 4, final: 'Declined', events: ['payment.pending', 'payment.declined'] },
] as const;
const REFUNDS = 10; // ZAR 5.00 each, on the first sim_card_success payments
const AMOUNT = '100.00'; // fee 2.9 % + ZAR 2.00 = 4.90, net 95.10 (docs/PAYMENT_LIFECYCLE.md §4)

test('Workers survive SIGKILL restarts without losing or duplicating work', async ({ baseURL }) => {
  test.setTimeout(600_000);

  // A clean receiver: the sink keeps its state in memory.
  compose('restart', 'webhook-sink');
  const merchant = await activeMerchant(baseURL!);
  const merchantUuid = scalar(`SELECT id FROM merchants.merchants WHERE trading_name = '${merchant.name}'`);
  expect(merchantUuid).toMatch(/^[0-9a-f-]{36}$/);

  const owner = await Dashboard.signIn(baseURL!, merchant.ownerEmail, merchant.ownerPassword);
  const created = await owner.post(`/api/merchants/${merchant.id}/webhook-endpoints`, {
    url: WebhookSink.endpointUrl,
    description: 'chaos receiver',
    eventTypes: ['*'],
  });
  expect(created.status()).toBe(201);
  const sink = await WebhookSink.connect();
  await expect.poll(async () => {
    try {
      await sink.configure({ secret: (await created.json()).secret, failNext: 0 });
      return true;
    } catch {
      return false;
    }
  }, { timeout: 30_000 }).toBe(true);
  await owner.dispose();

  const api = await MerchantApi.create(baseURL!, merchant.apiKey);
  try {
    // Phase 1: payments arrive steadily while the API is killed twice.
    const orders = PLAN.flatMap((p) => Array.from({ length: p.count }, (_, i) => ({ ...p, reference: `chaos-${p.method}-${i}`, key: randomUUID() })));
    const crashes = (async () => {
      await new Promise((r) => setTimeout(r, 2_000));
      await crashApi();
      await new Promise((r) => setTimeout(r, 6_000));
      await crashApi();
    })();
    const payments = await Promise.all(
      orders.map(async (o, i) => {
        await new Promise((r) => setTimeout(r, i * 300)); // ~12 s of traffic
        const res = await untilAnswered(() => api.pay({ amount: AMOUNT, paymentMethod: o.method, merchantReference: o.reference }, o.key));
        expect(res.status(), `${o.reference}: ${await res.text()}`).toBe(201);
        return { ...o, id: (await res.json()).id as string };
      }),
    );
    await crashes;

    // Phase 2: refunds, with a crash while the refund worker is processing them, and another while
    // pending payments are due and webhook deliveries are under way.
    const refundTargets = payments.filter((p) => p.method === 'sim_card_success').slice(0, REFUNDS);
    const refunded = new Set(refundTargets.map((p) => p.id));
    const refundCrash = (async () => {
      await new Promise((r) => setTimeout(r, 1_500));
      await crashApi();
      await new Promise((r) => setTimeout(r, 5_000));
      await crashApi(5_000);
    })();
    await Promise.all(
      refundTargets.map(async (p) => {
        const key = randomUUID();
        const res = await untilAnswered(() => api.refund(p.id, { amount: '5.00', reason: 'chaos' }, key));
        expect(res.status(), `${p.id}: ${await res.text()}`).toBe(201);
      }),
    );
    await refundCrash;
    // Evidence that the crashes really hit live traffic: requests that needed a retry.
    test.info().annotations.push({ type: 'interrupted requests', description: String(interrupted.count) });
    expect(interrupted.count, 'requests interrupted by the crashes').toBeGreaterThan(0);

    // Convergence: recovery and retry workers finish everything the crashes interrupted. A webhook
    // attempt cut off mid-flight is retried after its back-off (first retry 1 min ± 20 %).
    const pendingWork = () =>
      Number(scalar(`
        SELECT (SELECT count(*) FROM payments.payments WHERE merchant_id = '${merchantUuid}' AND status IN ('Created', 'Processing', 'Pending'))
             + (SELECT count(*) FROM refunds.refunds WHERE merchant_id = '${merchantUuid}' AND status = 'Pending')
             + (SELECT count(*) FROM platform.outbox_messages WHERE processed_at IS NULL)
             + (SELECT count(*) FROM webhooks.deliveries WHERE merchant_id = '${merchantUuid}' AND status <> 'Succeeded')`));
    await expect.poll(pendingWork, { timeout: 360_000, intervals: [5_000] }).toBe(0);

    // Exactly one payment per Idempotency-Key, each with its simulated outcome.
    expect(Number(scalar(`SELECT count(*) FROM payments.payments WHERE merchant_id = '${merchantUuid}'`))).toBe(orders.length);
    const statusByReference = new Map(sql(`SELECT merchant_reference, status FROM payments.payments WHERE merchant_id = '${merchantUuid}'`).map(([r, s]) => [r, s]));
    for (const p of payments) {
      const expected = refunded.has(p.id) ? 'PartiallyRefunded' : p.final;
      expect(statusByReference.get(p.reference), p.reference).toBe(expected);
    }

    // Each refund completed exactly once.
    expect(sql(`SELECT status, amount = 5.00, count(*) FROM refunds.refunds WHERE merchant_id = '${merchantUuid}' GROUP BY 1, 2`)).toEqual([['Succeeded', 't', String(REFUNDS)]]);

    // One event per state change: nothing lost, nothing duplicated by a replayed outbox message.
    const expectedEvents = new Map<string, number>();
    for (const p of PLAN) {
      for (const e of p.events) {
        expectedEvents.set(e, (expectedEvents.get(e) ?? 0) + p.count);
      }
    }
    expectedEvents.set('refund.succeeded', REFUNDS);
    const events = new Map(sql(`SELECT type, count(*) FROM webhooks.events WHERE merchant_id = '${merchantUuid}' GROUP BY 1`).map(([t, n]) => [t, Number(n)]));
    expect(Object.fromEntries(events)).toEqual(Object.fromEntries(expectedEvents));
    const totalEvents = [...expectedEvents.values()].reduce((a, b) => a + b, 0);

    // One delivery per event to the endpoint, all succeeded; the receiver verified each signature.
    expect(Number(scalar(`SELECT count(DISTINCT event_id) FROM webhooks.deliveries WHERE merchant_id = '${merchantUuid}' AND status = 'Succeeded'`))).toBe(totalEvents);
    expect(Number(scalar(`SELECT count(*) FROM webhooks.deliveries WHERE merchant_id = '${merchantUuid}'`))).toBe(totalEvents);
    const received = await sink.received();
    expect(received.filter((e) => e.verification !== 'Valid'), 'deliveries with a bad signature').toEqual([]);
    expect(new Set(received.map((e) => e.eventId)).size, 'distinct events the receiver accepted').toBe(totalEvents);

    // The ledger balances, entry by entry, and the merchant is owed exactly the calm-run amount.
    expect(Number(scalar(`
      SELECT count(*) FROM (
        SELECT journal_entry_id FROM ledger.postings GROUP BY 1
        HAVING sum(CASE WHEN direction = 'Debit' THEN amount ELSE -amount END) <> 0) unbalanced`))).toBe(0);
    const captured = PLAN.filter((p) => p.final === 'Succeeded').reduce((n, p) => n + p.count, 0);
    expect(Number(scalar(`
      SELECT coalesce(sum(CASE WHEN p.direction = 'Credit' THEN p.amount ELSE -p.amount END), 0)
        FROM ledger.postings p JOIN ledger.accounts a ON a.id = p.account_id
       WHERE a.kind = 'MerchantPayable' AND a.currency = 'ZAR' AND a.merchant_id = '${merchantUuid}'`))).toBeCloseTo(captured * 95.1 - REFUNDS * 5, 2);
    expect(Number(scalar(`SELECT count(*) FROM ledger.journal_entries j JOIN payments.payments p ON p.id = j.source_id WHERE p.merchant_id = '${merchantUuid}' AND j.entry_type = 'PaymentCaptured'`))).toBe(captured);
  } finally {
    await api.dispose();
    await sink.dispose();
    // Leave the stack running whatever happened.
    compose('up', '-d', 'api');
  }
});

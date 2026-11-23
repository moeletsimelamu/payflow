import { expect, test } from '@playwright/test';
import { MerchantApi, WebhookSink, activeMerchant, readSecret, signIn } from '../support/payflow';

/**
 * Scenario 6: webhook configuration and delivery attempts (WH-1..7), against the reference receiver
 * in docker-compose.yml. The first delivery is made to fail, so the run waits for PayFlow's first
 * scheduled retry (1 minute ± 20 % jitter) instead of faking the clock.
 */
test('6. Webhook config and delivery attempts (WH-1..7)', async ({ baseURL, page }) => {
  test.setTimeout(240_000);
  const merchant = await activeMerchant(baseURL!);
  const api = await MerchantApi.create(baseURL!, merchant.apiKey);
  const sink = await WebhookSink.connect();

  try {
    await signIn(page, merchant.ownerEmail, merchant.ownerPassword);
    await page.getByRole('link', { name: 'Webhooks' }).click();
    await expect(page.getByRole('heading', { name: 'Webhooks', level: 1 })).toBeVisible();
    const form = page.getByRole('form', { name: 'Add a webhook endpoint' });
    const notice = page.locator('section[aria-labelledby="endpoints"] > p[role="status"]');

    // WH-7: the API refuses URLs carrying credentials (the full private-address guard is covered by
    // the integration tests; the Compose demo allows private networks so it can reach the sink).
    await form.getByLabel('HTTPS URL').fill('https://user:secret@hooks.example.test/payflow');
    await form.getByRole('button', { name: 'Add endpoint' }).click();
    await expect(notice).toContainText('That URL is not allowed');

    // WH-1: an endpoint for one chosen event; its signing secret is shown once.
    await form.getByLabel('HTTPS URL').fill(WebhookSink.endpointUrl);
    await form.getByLabel('Description (optional)').fill('E2E reference receiver');
    await form.getByLabel('All events').uncheck();
    await form.getByLabel('payment.succeeded', { exact: true }).check();
    await form.getByRole('button', { name: 'Add endpoint' }).click();
    const secret = await readSecret(page, `Signing secret for ${WebhookSink.endpointUrl}`);
    expect(secret).toMatch(/^whsec_/);
    await page.getByRole('button', { name: 'I have stored it' }).click();
    const endpoint = page.locator('ul.pf-endpoints li').filter({ hasText: WebhookSink.endpointUrl });
    await expect(endpoint).toContainText('events: payment.succeeded');
    await expect(endpoint).toContainText(`secret ends …${secret.slice(-4)}`);

    // The receiver verifies signatures with that secret and fails the next delivery once.
    await sink.configure({ secret, failNext: 1 });

    // WH-2/3: a payment raises payment.succeeded with its own evt_ id, signed and delivered.
    const created = await api.pay({ amount: '25.00' });
    expect(created.status()).toBe(201);

    const deliveries = page.locator('section[aria-labelledby="deliveries"]');
    const row = deliveries.locator('tbody tr').filter({ hasText: 'payment.succeeded' }).first();
    const refresh = () => deliveries.getByRole('button', { name: 'Apply' }).click();

    // WH-4/5: the failed attempt is recorded and a retry is scheduled.
    await expect(async () => {
      await refresh();
      await expect(row).toContainText('Retrying', { timeout: 2_000 });
    }).toPass({ timeout: 30_000, intervals: [1_000, 2_000] });
    await expect(row).toContainText('next');
    const eventId = (await row.locator('code', { hasText: /^evt_/ }).textContent())!.trim();
    expect(eventId).toMatch(/^evt_[0-9a-z]+$/);

    await row.getByRole('button', { name: 'Show attempts' }).click();
    await expect(deliveries.locator('ol.pf-attempts')).toContainText(/Attempt 1 at .*:\s+HTTP 500/);
    const [failed] = await sink.received(eventId);
    expect(failed).toMatchObject({ eventType: 'payment.succeeded', attempt: '1', verification: 'Valid', responseStatus: 500 });
    await row.getByRole('button', { name: 'Hide attempts' }).click();

    // PayFlow retries on its own after the back-off; the receiver accepts the second attempt.
    await expect(async () => {
      await refresh();
      await expect(row).toContainText('Succeeded', { timeout: 2_000 });
    }).toPass({ timeout: 150_000, intervals: [5_000] });
    await expect(row.locator('td.pf-num')).toHaveText('2');
    await row.getByRole('button', { name: 'Show attempts' }).click();
    const attempts = deliveries.locator('ol.pf-attempts');
    await expect(attempts).toContainText(/Attempt 2 at .*:\s+HTTP 200/);
    await expect(attempts).toContainText(`"received":"${eventId}"`); // truncated response body is kept
    const delivered = (await sink.received(eventId)).find((e) => e.attempt === '2');
    expect(delivered).toMatchObject({ verification: 'Valid', duplicate: false, responseStatus: 200 });

    // WH-4/6: manual redelivery sends the same event id again; the receiver recognises the duplicate.
    await row.getByRole('button', { name: 'Redeliver' }).click();
    await expect(notice).toContainText('Redelivery of payment.succeeded queued');
    await expect.poll(async () => (await sink.received(eventId)).some((e) => e.duplicate && e.verification === 'Valid'), { timeout: 30_000 }).toBe(true);

    // WH-1: rolling the secret shows the new one once; the old one keeps signing for a grace period.
    await endpoint.getByRole('button', { name: 'Roll secret' }).click();
    const rolled = await readSecret(page, `Signing secret for ${WebhookSink.endpointUrl}`);
    expect(rolled).toMatch(/^whsec_/);
    expect(rolled).not.toBe(secret);
    await page.getByRole('button', { name: 'I have stored it' }).click();
    await expect(endpoint).toContainText('The previous secret also signs until');

    // A ping signed after the roll verifies against the new secret.
    await sink.configure({ secret: rolled });
    await endpoint.getByRole('button', { name: 'Send ping' }).click();
    await expect(notice).toContainText('Ping queued');
    await expect
      .poll(async () => (await sink.received()).some((e) => e.eventType === 'webhook.ping' && e.verification === 'Valid' && Date.parse(e.receivedAt) > Date.now() - 60_000), { timeout: 30_000 })
      .toBe(true);

    // Disabling the endpoint stops deliveries to it.
    await endpoint.getByRole('button', { name: 'Disable' }).click();
    await expect(notice).toContainText('Endpoint disabled');
    await expect(endpoint.getByRole('button', { name: 'Enable' })).toBeVisible();
  } finally {
    await sink.configure({ failNext: 0 });
    await sink.dispose();
    await api.dispose();
  }
});

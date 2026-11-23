import { Page, expect, test } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { MerchantApi, activeMerchant, adminCredentials, newPassword, readSecret, runTag, signIn, signOut } from '../support/payflow';

test('1. Register and manage a merchant (MER-1..7)', async ({ page }) => {
  const tag = runTag();
  const ownerEmail = `owner-${tag}@e2e.test`;
  const admin = adminCredentials();

  // Platform admin registers the merchant with its first admin.
  await signIn(page, admin.email, admin.password);
  await page.goto('/admin/merchants');
  await page.getByRole('link', { name: 'Register a merchant' }).click();
  await page.getByLabel('Legal name').fill(`Scenario One ${tag} (Pty) Ltd`);
  await page.getByLabel('Trading name').fill(`Scenario One ${tag}`);
  await page.getByLabel('Contact email').fill(`ops-${tag}@e2e.test`);
  await page.getByLabel('Email', { exact: true }).fill(ownerEmail);
  await page.getByLabel('Name', { exact: true }).fill(`Owner ${tag}`);
  await page.getByRole('button', { name: 'Register merchant' }).click();
  await expect(page.getByText(`Scenario One ${tag} is registered`)).toBeVisible();
  const invitation = await readSecret(page, /Invitation link/);
  expect(invitation).toContain('/accept-invitation#');
  expect(invitation).not.toContain('?'); // the token never goes in a query string

  // ...activates it...
  await page.getByRole('link', { name: 'Open the merchant' }).click();
  await expect(page).toHaveURL(/\/merchants\/mer_[0-9a-z]+$/);
  await expect(page.getByText('Pending activation', { exact: true }).first()).toBeVisible(); // the status badge
  await page.getByRole('button', { name: 'Activate' }).click();
  await expect(page.getByText('Status is now Active.')).toBeVisible();
  await signOut(page);

  // The invited owner sets a password from the one-time link and signs in.
  const password = newPassword();
  await page.goto(invitation.replace(/^https?:\/\/[^/]+/, ''));
  await expect(page.getByLabel('Email')).toHaveValue(ownerEmail);
  await expect(page).toHaveURL(/\/accept-invitation$/); // token cleared from the address bar
  await page.getByLabel('New password').fill(password);
  await page.getByLabel('Repeat the password').fill(password);
  await page.getByRole('button', { name: 'Set password' }).click();
  await expect(page.getByText('Your password is set.')).toBeVisible();
  await signIn(page, ownerEmail, password);
  await expect(page.getByRole('heading', { name: `Scenario One ${tag}` })).toBeVisible();

  // ...and issues an API key, shown once.
  await page.getByRole('link', { name: 'API keys' }).click();
  await page.getByPlaceholder('e.g. Checkout server').fill('Checkout server');
  await page.getByRole('button', { name: 'Create key' }).click();
  const secret = await readSecret(page, /Secret for Checkout server/);
  expect(secret).toMatch(/^pf_test_sk_/);
  await page.getByRole('button', { name: 'I have stored it' }).click();
  await expect(page.getByText(secret)).toHaveCount(0);

  // The key works against the merchant API.
  const api = await MerchantApi.create(page.url().replace(/^(https?:\/\/[^/]+).*$/, '$1'), secret);
  expect((await api.get('/v1/merchant')).status()).toBe(200);
  await api.dispose();
});

test.describe('payments', () => {
  // One owner session for the whole group, so the suite stays under the sign-in rate limit.
  test.describe.configure({ mode: 'serial' });
  let merchant: Awaited<ReturnType<typeof activeMerchant>>;
  let api: MerchantApi;
  let page: Page;

  test.beforeAll(async ({ baseURL, browser }) => {
    merchant = await activeMerchant(baseURL!);
    api = await MerchantApi.create(baseURL!, merchant.apiKey);
    page = await browser.newPage();
    await signIn(page, merchant.ownerEmail, merchant.ownerPassword);
  });

  test.afterAll(async () => {
    await api.dispose();
    await page.close();
  });

  test('2–3. Initiate a simulated payment and view its lifecycle (PAY-1..5, TXN-1..4)', async () => {
    const reference = `order-${runTag()}`;
    const created = await api.pay({ amount: '250.00', merchantReference: reference });
    expect(created.status()).toBe(201);
    const payment = await created.json();
    expect(payment.status).toBe('Succeeded');
    expect(payment.simulated).toBe(true);

    await page.goto(`/merchants/${merchant.id}`);
    await page.getByRole('link', { name: 'Transactions', exact: true }).click();
    await page.getByLabel('Merchant reference').fill(reference);
    await page.getByRole('button', { name: 'Apply' }).click();
    await expect(page.getByText('1 transaction')).toBeVisible();
    await page.getByRole('link', { name: reference }).click();

    await expect(page.getByRole('heading', { name: /ZAR 250\.00/ })).toBeVisible();
    const lifecycle = page.getByRole('region', { name: 'Lifecycle' }).or(page.locator('section[aria-labelledby="timeline"]'));
    await expect(lifecycle).toContainText('Processing');
    await expect(lifecycle).toContainText('Succeeded');
    const ledger = page.locator('section[aria-labelledby="ledger"]');
    await expect(ledger).toContainText('Payment captured');
    await expect(ledger).toContainText('Payment fee');
    await expect(ledger).toContainText('ZAR 250.00');
  });

  test('4. Retry without duplicates (IDEM-1..5)', async () => {
    const key = randomUUID();
    const reference = `retry-${runTag()}`;
    const first = await api.pay({ amount: '99.99', merchantReference: reference }, key);
    const second = await api.pay({ amount: '99.99', merchantReference: reference }, key);
    expect(first.status()).toBe(201);
    expect(second.status()).toBe(201);
    expect(second.headers()['idempotent-replayed']).toBe('true');
    expect(await second.text()).toBe(await first.text()); // byte-for-byte replay

    const changed = await api.pay({ amount: '100.00', merchantReference: reference }, key);
    expect(changed.status()).toBe(422);
    expect((await changed.json()).code).toBe('idempotency_key_reused');

    await page.goto(`/merchants/${merchant.id}/payments`);
    await page.getByLabel('Merchant reference').fill(reference);
    await page.getByRole('button', { name: 'Apply' }).click();
    await expect(page.getByText('1 transaction')).toBeVisible(); // one payment, however many retries
  });

  test('5. Partial refund (REF-1..4)', async () => {
    const reference = `refund-${runTag()}`;
    const payment = await (await api.pay({ amount: '100.00', merchantReference: reference })).json();

    await page.goto(`/merchants/${merchant.id}/payments/${payment.id}`);
    await page.getByRole('button', { name: 'Refund' }).click();
    const dialog = page.getByRole('dialog', { name: 'Refund payment' });
    await dialog.getByLabel(/Amount/).fill('100.01');
    await expect(dialog.getByRole('alert')).toContainText('At most ZAR 100.00 can still be refunded.');
    await dialog.getByLabel(/Amount/).fill('40.00');
    await dialog.getByLabel('Reason (optional)').fill('Scenario 5');
    await dialog.getByRole('button', { name: 'Refund', exact: true }).click();
    await expect(page.getByText(/Refund re_.* created/)).toBeVisible();

    // The refund worker completes it within seconds; the payment becomes partially refunded.
    await expect(async () => {
      await page.reload();
      await expect(page.getByRole('heading', { name: /ZAR 100\.00/ })).toContainText('Partially refunded');
    }).toPass({ timeout: 30_000, intervals: [1_000, 2_000, 3_000] });
    await expect(page.locator('dd').filter({ hasText: 'ZAR 60.00' }).first()).toBeVisible();
    await expect(page.locator('section[aria-labelledby="refunds"]')).toContainText('Scenario 5');
  });
});

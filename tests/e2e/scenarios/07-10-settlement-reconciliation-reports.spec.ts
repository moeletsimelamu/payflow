import { Page, expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { MerchantApi, activeMerchant, adminCredentials, cents, signIn } from '../support/payflow';

/**
 * Scenarios 7–10 on one fresh merchant with known activity: three successful payments, one
 * decline and a partial refund. The platform admin settles it, reconciles a generated statement
 * and works the exceptions; the merchant owner checks the payout and both read the reports.
 */
test.describe('settlement, reconciliation and reports', () => {
  test.describe.configure({ mode: 'serial' });

  let merchant: Awaited<ReturnType<typeof activeMerchant>>;
  let api: MerchantApi;
  let admin: Page;
  let owner: Page;
  const paymentIds: string[] = [];
  let refundId = '';
  let payout = 0;

  test.beforeAll(async ({ baseURL, browser }) => {
    test.setTimeout(120_000);
    merchant = await activeMerchant(baseURL!);
    api = await MerchantApi.create(baseURL!, merchant.apiKey);
    for (const amount of ['100.00', '250.00', '49.99']) {
      const created = await api.pay({ amount });
      expect(created.status()).toBe(201);
      paymentIds.push((await created.json()).id);
    }
    const declined = await (await api.pay({ amount: '75.00', paymentMethod: 'sim_card_declined_insufficient_funds' })).json();
    expect(declined.status).toBe('Declined');

    const refund = await api.refund(paymentIds[0], { amount: '20.00', reason: 'Scenario 7' });
    expect(refund.status()).toBe(201);
    refundId = (await refund.json()).id;
    await expect.poll(async () => (await (await api.get(`/v1/payments/${paymentIds[0]}`)).json()).status, { timeout: 30_000 }).toBe('PartiallyRefunded');

    admin = await browser.newPage();
    owner = await browser.newPage();
    await signIn(admin, adminCredentials().email, adminCredentials().password);
    await signIn(owner, merchant.ownerEmail, merchant.ownerPassword);
  });

  test.afterAll(async () => {
    await api?.dispose();
    await admin?.close();
    await owner?.close();
  });

  test('7. Generate a settlement (SET-1..5)', async () => {
    test.setTimeout(150_000);
    // The cut-off field has minute precision and may not be in the future: wait for the next minute
    // so a cut-off of "now" includes everything created above.
    await admin.waitForTimeout(60_000 - (Date.now() % 60_000) + 3_000);
    const cutoff = new Date(Math.floor(Date.now() / 60_000) * 60_000).toISOString().slice(0, 16);

    await admin.goto('/admin/settlements');
    const runForm = admin.getByRole('form', { name: 'Settlement run' });
    await runForm.getByLabel('Merchant id (optional)').fill(merchant.id);
    await runForm.getByLabel('Cut-off (UTC, optional)').fill(cutoff);
    await runForm.getByRole('button', { name: 'Run settlement' }).click();
    const runStatus = admin.locator('section[aria-labelledby="run"] [role="status"]');
    await expect(runStatus).toContainText('1 batch created, 0 skipped');

    // SET-3: Pending → Failed (simulated bank rejection) → retried → Paid.
    const batches = admin.locator('section[aria-labelledby="batches"]');
    const row = batches.locator('tbody tr').filter({ hasText: merchant.id });
    await expect(row).toHaveCount(1);
    await expect(row).toContainText('Pending');
    await expect(row.locator('td.pf-num').first()).toHaveText('4'); // 3 payments + 1 refund
    await batches.getByLabel('Simulate a bank rejection on the next payout').check();
    await row.getByRole('button', { name: 'Pay out' }).click();
    const payStatus = batches.locator('p[role="status"]');
    await expect(payStatus).toContainText('failed');
    await expect(payStatus).toContainText('It can be retried.');
    await expect(row).toContainText('Failed');
    await row.getByRole('button', { name: 'Retry payout' }).click();
    await expect(payStatus).toContainText(/paid, reference \S+/);
    await expect(row).toContainText('Paid');

    // SET-4: nothing is left to settle, so a second run creates no batch.
    await runForm.getByRole('button', { name: 'Run settlement' }).click();
    await expect(runStatus).toContainText('0 batches created');

    // SET-2/5: the merchant sees how the payout was calculated, item by item.
    await owner.goto(`/merchants/${merchant.id}/settlements`);
    const history = owner.locator('tbody tr');
    await expect(history).toHaveCount(1);
    await expect(history).toContainText('Paid');
    await history.getByRole('link').click();
    await expect(owner.getByRole('heading', { level: 1 })).toContainText('Paid');
    const calc = owner.locator('section[aria-labelledby="calc"]');
    const figure = async (label: string | RegExp) => cents(await calc.getByRole('row', { name: label }).locator('td').textContent());
    expect(await figure(/^Gross payments \(3\)/)).toBe(39_999);
    expect(await figure(/^Refunds \(1\)/)).toBe(2_000);
    const fees = await figure(/^Fees/);
    expect(fees).toBeGreaterThan(0);
    const net = await figure(/^Net/);
    expect(net).toBe(39_999 - fees - 2_000);
    payout = await figure(/^Payout/);
    expect(payout).toBe(net);
    await expect(calc).toContainText(/Paid .*, reference \S+/);

    const items = owner.locator('section[aria-labelledby="items"] tbody tr');
    await expect(items).toHaveCount(4);
    for (const id of [...paymentIds, refundId]) {
      await expect(items.filter({ hasText: id })).toHaveCount(1);
    }

    const [download] = await Promise.all([owner.waitForEvent('download'), owner.getByRole('link', { name: 'Download line items (CSV)' }).click()]);
    const csv = readFileSync(await download.path(), 'utf8').trim().split(/\r?\n/);
    expect(csv).toHaveLength(5); // header + 4 items
    const sourceColumn = csv[0].split(',').indexOf('source_id');
    expect(sourceColumn).toBeGreaterThanOrEqual(0);
    expect(csv.slice(1).map((line) => line.split(',')[sourceColumn]).sort()).toEqual([...paymentIds, refundId].sort());
  });

  let injected: { outcome: string; reference: string }[] = [];
  let fileName = '';
  let lineCount = 0;

  test('8. Import a statement (REC-1..2)', async () => {
    await admin.goto('/admin/reconciliation');
    const statement = admin.locator('section[aria-labelledby="import"]');
    const generator = admin.getByRole('form', { name: 'Generate a statement' });

    // REC-2: the simulated acquirer's statement for today, with one of every discrepancy planted.
    for (const kind of ['Missing external', 'Missing internal', 'Amount mismatch', 'Currency mismatch', 'Duplicate external', 'Status discrepancy']) {
      await generator.getByLabel(new RegExp(`^${kind}$`, 'i')).fill('1');
    }
    const [generated] = await Promise.all([
      admin.waitForResponse((r) => r.request().method() === 'POST' && r.url().endsWith('/api/reconciliation/statements/generate')),
      generator.getByRole('button', { name: 'Generate' }).click(),
    ]);
    expect(generated.status()).toBe(200);
    const body = await generated.json();
    injected = body.injected;
    fileName = body.fileName;
    lineCount = body.lineCount;
    expect(injected.map((i) => i.outcome).sort()).toEqual(['AmountMismatch', 'CurrencyMismatch', 'DuplicateExternal', 'MissingExternal', 'MissingInternal', 'StatusDiscrepancy']);
    await expect(statement.locator('p[role="status"]').first()).toContainText(`Generated ${fileName}: ${lineCount} lines. Planted:`);
    const csv = await statement.getByLabel('Statement CSV').inputValue();
    expect(csv.split(/\r?\n/)[0]).toMatch(/^[a-z_]+(,[a-z_]+)+$/i); // a header row

    // REC-1: import it...
    await statement.getByRole('button', { name: 'Import' }).click();
    await expect(statement.locator('p.pf-notice')).toContainText(`Imported ${fileName} (${lineCount} lines).`);

    // ...the same file again is rejected by its content hash...
    await statement.getByLabel('Statement CSV').fill(csv);
    await statement.getByRole('button', { name: 'Import' }).click();
    await expect(statement.getByRole('alert')).toContainText('This statement file has already been imported.');

    // ...and so is something that is not a statement.
    await statement.getByLabel('Statement CSV').fill('this is not,a statement\n1,2');
    await statement.getByRole('button', { name: 'Import' }).click();
    await expect(statement.getByRole('alert')).toContainText('The statement was not imported:');

    const newest = admin.locator('section[aria-labelledby="statements"] tbody tr').first();
    await expect(newest).toContainText(fileName);
    await expect(newest.locator('td.pf-num')).toHaveText(String(lineCount));
    await expect(newest).toContainText('Imported');
  });

  test('9. Identify discrepancies (REC-3..5)', async () => {
    await admin.goto('/admin/reconciliation');
    const newest = admin.locator('section[aria-labelledby="statements"] tbody tr').first();
    await expect(newest).toContainText(fileName);

    // REC-3: every planted discrepancy is classified.
    await newest.getByRole('button', { name: 'Run matching' }).click();
    const summary = admin.locator('.pf-run-summary');
    for (const label of ['Missing from statement', 'Unknown to PayFlow', 'Amount mismatch', 'Currency mismatch', 'Duplicate on statement', 'Status discrepancy']) {
      await expect(summary.getByRole('row', { name: `${label} 1`, exact: true })).toBeVisible();
    }
    await expect(summary).toContainText('6 new exceptions, 0 seen again.');

    const queue = admin.locator('section[aria-labelledby="queue"]');
    const notice = admin.locator('section[aria-labelledby="import"] p.pf-notice');
    await expect(queue.locator('tbody tr')).toHaveCount(6);
    const refOf = (outcome: string) => injected.find((i) => i.outcome === outcome)!.reference;
    const rowFor = (outcome: string, words: string) => queue.locator('tbody tr').filter({ hasText: refOf(outcome) }).filter({ hasText: words });
    const dialog = admin.getByRole('dialog');

    // REC-4: Open → InReview → Resolved, with a mandatory note.
    await rowFor('AmountMismatch', 'Amount mismatch').getByRole('button', { name: 'Work' }).click();
    await expect(dialog).toContainText('Amount mismatch');
    await dialog.getByRole('button', { name: 'Save' }).click(); // "Start review" is the default for an open exception
    await expect(notice).toContainText(/Exception \S+ is now InReview\./);

    await queue.getByRole('combobox', { name: 'Status' }).click();
    await admin.getByRole('option', { name: 'Any' }).click();
    await queue.getByRole('button', { name: 'Apply' }).click();
    await rowFor('AmountMismatch', 'Amount mismatch').getByRole('button', { name: 'Work' }).click();
    await dialog.getByRole('button', { name: 'Save' }).click(); // resolve without a note
    await expect(dialog.getByRole('alert')).toHaveText('A note explaining the decision is required.');
    await dialog.getByLabel('Note').fill('Acquirer confirmed PayFlow amount (E2E).');
    await dialog.getByRole('button', { name: 'Save' }).click();
    await expect(notice).toContainText(/Exception \S+ is now Resolved\./);

    // ...or Open → Dismissed.
    await rowFor('MissingInternal', 'Missing internal').getByRole('button', { name: 'Work' }).click();
    await dialog.getByRole('radio', { name: 'Dismiss as a false positive' }).check();
    await dialog.getByLabel('Note').fill('Test line from the simulated acquirer (E2E).');
    await dialog.getByRole('button', { name: 'Save' }).click();
    await expect(notice).toContainText(/Exception \S+ is now Dismissed\./);

    // The decision is recorded with actor and note.
    await rowFor('AmountMismatch', 'Amount mismatch').getByRole('button', { name: 'View' }).click();
    await expect(dialog).toContainText(/Closed as Accepted internal by usr_[0-9a-z]+ on /); // the actor is the signed-in admin's user id
    await expect(dialog).toContainText('Acquirer confirmed PayFlow amount (E2E).');
    await dialog.getByRole('button', { name: 'Close' }).click();

    // REC-5: a rerun is deterministic and raises nothing new; closed exceptions stay closed.
    await newest.getByRole('button', { name: 'Run again' }).click();
    await expect(summary).toContainText('0 new exceptions, 6 seen again.');
    await queue.getByRole('combobox', { name: 'Status' }).click();
    await admin.getByRole('option', { name: 'Any' }).click();
    await queue.getByRole('button', { name: 'Apply' }).click();
    await expect(queue.locator('tbody tr')).toHaveCount(6);
    await expect(rowFor('AmountMismatch', 'Amount mismatch')).toContainText('Resolved');
    await expect(rowFor('MissingInternal', 'Missing internal')).toContainText('Dismissed');
  });

  test('10. Financial and operational reports (RPT-1..6)', async () => {
    test.setTimeout(120_000);
    const kpi = (page: Page, label: string) => page.locator('.pf-kpi').filter({ has: page.locator('.pf-kpi-label', { hasText: new RegExp(`^${label}$`) }) }).locator('.pf-kpi-value');

    // The merchant's own figures, exact for this merchant. Reports are cached for up to a minute,
    // and the overview already asked for them before the payout, so reload until they catch up.
    await expect(async () => {
      await owner.goto(`/merchants/${merchant.id}/reports`);
      await expect(kpi(owner, 'Paid out')).toHaveText(`ZAR ${(payout / 100).toFixed(2)}`, { timeout: 3_000 });
    }).toPass({ timeout: 90_000, intervals: [5_000] });
    await expect(owner.getByRole('heading', { name: 'Reports', level: 1 })).toBeVisible();
    await expect(kpi(owner, 'Payments')).toHaveText('4'); // RPT-1
    await expect(kpi(owner, 'Succeeded value')).toHaveText('ZAR 399.99');
    await expect(kpi(owner, 'Success rate')).toHaveText('75.00%'); // RPT-2
    await expect(kpi(owner, 'Refunded')).toHaveText('ZAR 20.00'); // RPT-3
    await expect(owner.locator('.pf-kpi').filter({ hasText: 'Refunded' })).toContainText('5.00% of value');
    const rates = owner.locator('section[aria-labelledby="rates"]');
    await expect(rates.getByRole('row', { name: 'insufficient_funds 1' })).toBeVisible();
    const settle = owner.locator('section[aria-labelledby="settle"]'); // RPT-4
    await expect(settle).toContainText('1 (1 paid, 0 pending, 0 failed)');
    await expect(settle).toContainText('1 · ZAR 20.00');
    await expect(owner.locator('section[aria-labelledby="perf"]')).toContainText('Payments processed synchronously'); // RPT-6
    await expect(owner.locator('section[aria-labelledby="rec"]')).toHaveCount(0); // the backlog is for platform admins

    // The platform view adds the reconciliation backlog by type and age (RPT-5) and webhook figures.
    await admin.goto('/admin/reports');
    await expect(admin.getByRole('heading', { name: 'Platform reports', level: 1 })).toBeVisible();
    expect(Number(await kpi(admin, 'Payments').textContent())).toBeGreaterThanOrEqual(4);
    const backlog = admin.locator('section[aria-labelledby="rec"]');
    await expect(backlog).toContainText(/\d+ unresolved exceptions?, all currencies\./);
    await expect(backlog.getByRole('table')).not.toHaveCount(0);
    await expect(admin.locator('section[aria-labelledby="perf"]')).toContainText('Webhook latency p50 / p95');
  });
});

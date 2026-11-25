import { APIRequestContext, APIResponse, Page, expect, request } from '@playwright/test';
import { randomBytes, randomUUID } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { freshTotp } from './totp';

/** One value from the repository's .env, the file docker compose used to start the stack. */
function fromDotEnv(key: string): string | undefined {
  const envFile = resolve(__dirname, '../../../.env');
  if (!existsSync(envFile)) {
    return undefined;
  }
  const lines = readFileSync(envFile, 'utf8').split(/\r?\n/);
  return lines.find((l) => l.startsWith(`${key}=`))?.slice(key.length + 1).trim() || undefined;
}

/**
 * The platform admin the stack was seeded with. CI passes PAYFLOW_E2E_ADMIN_EMAIL/PASSWORD;
 * locally they are read from the repository's .env (PAYFLOW_ADMIN_EMAIL/PASSWORD), the same values
 * docker compose used to seed the admin.
 */
export function adminCredentials(): { email: string; password: string; totpSecret: string } {
  const email = process.env['PAYFLOW_E2E_ADMIN_EMAIL'] ?? fromDotEnv('PAYFLOW_ADMIN_EMAIL');
  const password = process.env['PAYFLOW_E2E_ADMIN_PASSWORD'] ?? fromDotEnv('PAYFLOW_ADMIN_PASSWORD');
  // Platform admins need an authenticator code (SECURITY.md, SEC-2): the seeded admin's secret.
  const totpSecret = process.env['PAYFLOW_E2E_ADMIN_TOTP_SECRET'] ?? fromDotEnv('PAYFLOW_ADMIN_TOTP_SECRET');
  if (!email || !password || !totpSecret) {
    throw new Error(
      'Set PAYFLOW_E2E_ADMIN_EMAIL, PAYFLOW_E2E_ADMIN_PASSWORD and PAYFLOW_E2E_ADMIN_TOTP_SECRET, or PAYFLOW_ADMIN_EMAIL / PAYFLOW_ADMIN_PASSWORD / PAYFLOW_ADMIN_TOTP_SECRET in .env.',
    );
  }
  return { email, password, totpSecret };
}

/**
 * The reference webhook receiver from docker-compose.yml (tools/webhook-sink), on its loopback-only
 * host port. The API reaches the same container as http://webhook-sink:8080 on the Compose network.
 * The sink keeps one signing secret for everything it receives; restart it to reset it.
 */
export class WebhookSink {
  static readonly endpointUrl = 'http://webhook-sink:8080/webhooks';

  private constructor(private readonly ctx: APIRequestContext) {}

  static async connect(): Promise<WebhookSink> {
    const base = process.env['PAYFLOW_E2E_SINK_URL'] ?? `http://127.0.0.1:${fromDotEnv('PAYFLOW_SINK_PORT') ?? '8090'}`;
    return new WebhookSink(await request.newContext({ baseURL: base }));
  }

  /** The secret the sink verifies signatures with, and how many deliveries it answers with HTTP 500. */
  async configure(config: { secret?: string; failNext?: number }): Promise<void> {
    expect((await this.ctx.post('/config', { data: config })).status(), 'sink /config').toBe(200);
  }

  /** What the sink received, newest first. */
  async received(eventId?: string): Promise<SinkEvent[]> {
    const all: SinkEvent[] = await (await this.ctx.get('/events')).json();
    return eventId ? all.filter((e) => e.eventId === eventId) : all;
  }

  dispose() {
    return this.ctx.dispose();
  }
}

export interface SinkEvent {
  eventId: string;
  eventType: string;
  attempt: string;
  verification: string;
  duplicate: boolean;
  receivedAt: string;
  responseStatus: number;
}

/** Minor units of a displayed amount such as "ZAR 1,234.50" (two-decimal currencies only). */
export function cents(display: string | null): number {
  const match = /(-?)([\d,]+)\.(\d{2})/.exec(display ?? '');
  if (!match) {
    throw new Error(`No amount in "${display}"`);
  }
  return (match[1] ? -1 : 1) * (Number(match[2].replaceAll(',', '')) * 100 + Number(match[3]));
}

/** A unique tag so every run creates its own merchants and users; the shared database keeps growing. */
export const runTag = () => randomBytes(4).toString('hex');

/** A strong throwaway password for users the tests create. Never logged. */
export const newPassword = () => `e2e-${randomBytes(12).toString('base64url')}`;

/**
 * PayFlow limits sign-in to 10 attempts a minute per client (a real control, not relaxed for tests).
 * The suites reuse sessions to stay under it; if a run still hits it, wait out the window once.
 */
const RATE_LIMIT_WINDOW_MS = 61_000;

/** Signs in through the dashboard; pass the authenticator secret for users with MFA (platform admins). */
export async function signIn(page: Page, email: string, password: string, totpSecret?: string): Promise<void> {
  for (let attempt = 0; attempt < 2; attempt++) {
    await page.goto('/login');
    await page.getByLabel('Email').fill(email);
    await page.getByLabel('Password').fill(password);
    await page.getByRole('button', { name: 'Sign in' }).click();
    const limited = page.getByText('Too many sign-in attempts');
    const secondStep = page.getByRole('heading', { name: 'Two-step verification' });
    let outcome = await Promise.race([
      page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 15_000 }).then(() => 'in' as const),
      limited.waitFor({ timeout: 15_000 }).then(() => 'limited' as const),
      secondStep.waitFor({ timeout: 15_000 }).then(() => 'code' as const),
    ]);
    if (outcome === 'code') {
      expect(totpSecret, `${email} needs an authenticator code`).toBeTruthy();
      await page.getByLabel('Authentication code').fill(await freshTotp(totpSecret!));
      await page.getByRole('button', { name: 'Verify' }).click();
      outcome = await Promise.race([
        page.waitForURL((url) => !url.pathname.startsWith('/login'), { timeout: 15_000 }).then(() => 'in' as const),
        page.getByText('Too many attempts').waitFor({ timeout: 15_000 }).then(() => 'limited' as const),
      ]);
    }
    if (outcome === 'in') {
      return;
    }
    await page.waitForTimeout(RATE_LIMIT_WINDOW_MS);
  }
  await expect(page, 'sign-in stayed rate limited').not.toHaveURL(/\/login/);
}

/** The seeded platform admin, signed in through the dashboard with password and authenticator code. */
export function signInAsAdmin(page: Page): Promise<void> {
  const admin = adminCredentials();
  return signIn(page, admin.email, admin.password, admin.totpSecret);
}

export async function signOut(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Sign out' }).click();
  await expect(page).toHaveURL(/\/login/);
}

/** The value shown in a one-time secret panel (API key, webhook secret, invitation link). */
export async function readSecret(page: Page, title: string | RegExp): Promise<string> {
  const panel = page.getByRole('region', { name: title });
  await expect(panel).toBeVisible();
  const value = (await panel.locator('code').first().textContent())?.trim() ?? '';
  expect(value).not.toBe('');
  return value;
}

/** A dashboard API session (cookie + XSRF), for test setup that is not itself under test. */
export class Dashboard {
  private constructor(private readonly ctx: APIRequestContext) {}

  static async signIn(baseURL: string, email: string, password: string, totpSecret?: string): Promise<Dashboard> {
    const ctx = await request.newContext({ baseURL });
    const dashboard = new Dashboard(ctx);
    await ctx.get('/api/auth/antiforgery');
    const limited = async (send: () => Promise<APIResponse>) => {
      const first = await send();
      if (first.status() !== 429) {
        return first;
      }
      await new Promise((r) => setTimeout(r, RATE_LIMIT_WINDOW_MS));
      return send();
    };
    let response = await limited(() => dashboard.post('/api/auth/login', { email, password }));
    if (response.status() === 202) {
      expect(totpSecret, `${email} needs an authenticator code`).toBeTruthy();
      const code = await freshTotp(totpSecret!);
      response = await limited(() => dashboard.post('/api/auth/mfa/verify', { code }));
    }
    expect(response.status(), 'dashboard sign-in').toBe(200);
    return dashboard;
  }

  async post(path: string, body: unknown, headers: Record<string, string> = {}) {
    const xsrf = (await this.ctx.storageState()).cookies.find((c) => c.name === 'XSRF-TOKEN')?.value ?? '';
    return this.ctx.post(path, { data: body, headers: { 'X-XSRF-TOKEN': xsrf, ...headers } });
  }

  get(path: string) {
    return this.ctx.get(path);
  }

  dispose() {
    return this.ctx.dispose();
  }
}

/** A merchant's server calling the merchant API with a bearer key. */
export class MerchantApi {
  private constructor(private readonly ctx: APIRequestContext) {}

  static async create(baseURL: string, secret: string): Promise<MerchantApi> {
    return new MerchantApi(await request.newContext({ baseURL, extraHTTPHeaders: { Authorization: `Bearer ${secret}` } }));
  }

  /** Creates a payment; pass the same key to retry it. */
  pay(body: { amount: string; currency?: string; paymentMethod?: string; merchantReference?: string }, idempotencyKey = randomUUID()) {
    return this.ctx.post('/v1/payments', {
      data: { currency: 'ZAR', paymentMethod: 'sim_card_success', merchantReference: `e2e-${runTag()}`, ...body },
      headers: { 'Idempotency-Key': idempotencyKey },
    });
  }

  /** Refunds part or all of a payment; the refund completes asynchronously. */
  refund(paymentId: string, body: { amount?: string; reason?: string }, idempotencyKey = randomUUID()) {
    return this.ctx.post(`/v1/payments/${paymentId}/refunds`, { data: body, headers: { 'Idempotency-Key': idempotencyKey } });
  }

  get(path: string) {
    return this.ctx.get(path);
  }

  dispose() {
    return this.ctx.dispose();
  }
}

let adminDashboard: Promise<Dashboard> | null = null;

/** One platform-admin API session per test worker, for setup. */
export function adminSession(baseURL: string): Promise<Dashboard> {
  const admin = adminCredentials();
  adminDashboard ??= Dashboard.signIn(baseURL, admin.email, admin.password, admin.totpSecret);
  return adminDashboard;
}

/**
 * An active merchant with a signed-in owner and an API key, built through the API: the setup most
 * scenarios need but do not test (scenario 1 builds the same thing through the UI).
 */
export async function activeMerchant(baseURL: string): Promise<{ id: string; name: string; ownerEmail: string; ownerPassword: string; apiKey: string }> {
  const tag = runTag();
  const admin = await adminSession(baseURL);
  const ownerEmail = `owner-${tag}@e2e.test`;
  const created = await admin.post('/api/merchants', {
    legalName: `E2E Shop ${tag} (Pty) Ltd`,
    tradingName: `E2E Shop ${tag}`,
    contactEmail: `ops-${tag}@e2e.test`,
    countryCode: 'ZA',
    currencies: [{ currency: 'ZAR', maxPaymentAmount: '50000.00', operatorRefundLimit: '500.00' }],
    adminEmail: ownerEmail,
    adminDisplayName: `Owner ${tag}`,
  });
  expect(created.status()).toBe(201);
  const body = await created.json();
  const id: string = body.merchant.id;
  expect((await admin.post(`/api/merchants/${id}/status`, { status: 'Active', reason: null })).status()).toBe(200);

  const ownerPassword = newPassword();
  const anonymous = await request.newContext({ baseURL });
  await anonymous.get('/api/auth/antiforgery');
  const xsrf = (await anonymous.storageState()).cookies.find((c) => c.name === 'XSRF-TOKEN')?.value ?? '';
  const accepted = await anonymous.post('/api/auth/accept-invitation', {
    data: { email: ownerEmail, token: body.adminInvitation.invitationToken, password: ownerPassword },
    headers: { 'X-XSRF-TOKEN': xsrf },
  });
  expect(accepted.status()).toBe(204);
  await anonymous.dispose();

  const owner = await Dashboard.signIn(baseURL, ownerEmail, ownerPassword);
  const key = await owner.post(`/api/merchants/${id}/api-keys`, { name: 'e2e' });
  expect(key.status()).toBe(201);
  const apiKey: string = (await key.json()).secret;
  await owner.dispose();
  return { id, name: `E2E Shop ${tag}`, ownerEmail, ownerPassword, apiKey };
}

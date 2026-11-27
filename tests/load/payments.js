// Load test for POST /v1/payments (docs/REQUIREMENTS.md §5: p95 < 150 ms, 100 req/s sustained).
//
//   docker compose --profile load run --rm k6
//
// k6 runs inside the gateway's network namespace, so https://localhost is Caddy itself and the
// certificate is verified against Caddy's local CA (SSL_CERT_FILE), never skipped.
//
// setup() builds the tenants through the dashboard API, the same way an operator would: the platform
// admin registers and activates merchants, each invited owner sets a password and issues API keys.
// Each key is limited to 600 requests a minute (a real control, left on), so the load is spread over
// MERCHANTS x KEYS_PER_MERCHANT keys: 2 x 10 keys at 100 req/s is about 300 requests per key a minute.

import http from 'k6/http';
import exec from 'k6/execution';
import { check, fail, sleep } from 'k6';
import k6crypto from 'k6/crypto';

const BASE = __ENV.PAYFLOW_BASE_URL || 'https://localhost';
const RATE = Number(__ENV.RATE || 100);
const DURATION = __ENV.DURATION || '2m';
const MERCHANTS = Number(__ENV.MERCHANTS || 2);
const KEYS_PER_MERCHANT = Number(__ENV.KEYS_PER_MERCHANT || 10);
// Every Nth payment is sent twice with the same Idempotency-Key, to exercise replays under load.
const REPLAY_EVERY = Number(__ENV.REPLAY_EVERY || 10);

/** Seconds in a k6 duration such as "2m", "90s" or "1m30s". */
function seconds(duration) {
  let total = 0;
  for (const [, n, unit] of duration.matchAll(/(\d+)(h|m|s)/g)) {
    total += Number(n) * { h: 3600, m: 60, s: 1 }[unit];
  }
  return total;
}

// Thresholds pass vacuously on zero samples, so also require that the sustained phase really ran.
const MIN_PAYMENTS = Math.floor(RATE * seconds(DURATION) * 0.95);

export const options = {
  setupTimeout: '2m',
  scenarios: {
    // Warms up connection pools, JIT and caches; not measured against the targets.
    warmup: {
      executor: 'constant-arrival-rate',
      rate: Math.max(1, Math.round(RATE / 5)),
      timeUnit: '1s',
      duration: '20s',
      preAllocatedVUs: 20,
      maxVUs: 100,
    },
    sustained: {
      executor: 'constant-arrival-rate',
      startTime: '20s',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 50,
      maxVUs: 300,
    },
  },
  thresholds: {
    'http_req_duration{scenario:sustained,name:create_payment}': ['p(95)<150'],
    'http_req_failed{scenario:sustained}': ['rate<0.01'],
    'checks{scenario:sustained}': ['rate>0.99'],
    // The target rate was actually reached: k6 had to skip almost no iterations.
    'dropped_iterations{scenario:sustained}': [`count<${Math.ceil(RATE * 2)}`],
    'http_reqs{scenario:sustained,name:create_payment}': [`count>=${MIN_PAYMENTS}`],
  },
  summaryTrendStats: ['min', 'med', 'avg', 'p(90)', 'p(95)', 'p(99)', 'max'],
};

function xsrfHeader(jar) {
  const token = jar.cookiesForURL(BASE)['XSRF-TOKEN'];
  if (!token || token.length === 0) {
    fail('no XSRF-TOKEN cookie; is the dashboard API reachable over HTTPS?');
  }
  return { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': token[0] };
}

function dashboardPost(jar, path, body, expected) {
  const res = http.post(`${BASE}${path}`, JSON.stringify(body), { jar, headers: xsrfHeader(jar), tags: { name: 'setup' } });
  if (res.status !== expected) {
    fail(`${path} returned ${res.status}, expected ${expected}: ${res.body}`);
  }
  return res;
}

/** RFC 6238 code (HMAC-SHA1, 30 s, 6 digits) for a Base32 secret, at a given step. */
function totp(secret, step) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  const bytes = [];
  let value = 0;
  let bits = 0;
  for (const c of secret.replace(/[\s=-]/g, '').toUpperCase()) {
    value = (value << 5) | alphabet.indexOf(c);
    bits += 5;
    if (bits >= 8) {
      bytes.push((value >>> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  const counter = new Uint8Array(8);
  const high = Math.floor(step / 2 ** 32);
  const low = step >>> 0;
  [high >>> 24, high >>> 16, high >>> 8, high, low >>> 24, low >>> 16, low >>> 8, low].forEach((b, i) => (counter[i] = b & 0xff));
  const hash = new Uint8Array(k6crypto.hmac('sha1', new Uint8Array(bytes).buffer, counter.buffer, 'binary'));
  const offset = hash[hash.length - 1] & 0x0f;
  const binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
  return String(binary % 1000000).padStart(6, '0');
}

/** Signs in to the dashboard API; platform admins also give an authenticator code (SECURITY.md, SEC-2). */
function signIn(email, password, totpSecret) {
  const jar = new http.CookieJar();
  http.get(`${BASE}/api/auth/antiforgery`, { jar, tags: { name: 'setup' } });
  const login = http.post(`${BASE}/api/auth/login`, JSON.stringify({ email, password }), { jar, headers: xsrfHeader(jar), tags: { name: 'setup' } });
  if (login.status === 200) {
    return jar;
  }
  if (login.status !== 202 || !totpSecret) {
    fail(`sign-in returned ${login.status}${login.status === 202 ? ': set PAYFLOW_ADMIN_TOTP_SECRET' : ''}`);
  }
  // A code is accepted once: if another tool just used this step's code, the next step's will do.
  for (let attempt = 0; attempt < 2; attempt++) {
    const step = Math.floor(Date.now() / 30000);
    const verify = http.post(`${BASE}/api/auth/mfa/verify`, JSON.stringify({ code: totp(totpSecret, step) }), { jar, headers: xsrfHeader(jar), tags: { name: 'setup' } });
    if (verify.status === 200) {
      return jar;
    }
    sleep(((step + 1) * 30000 - Date.now()) / 1000 + 0.5);
  }
  fail('the authenticator code was refused twice; check PAYFLOW_ADMIN_TOTP_SECRET');
}

function waitUntilReady() {
  for (let i = 0; i < 60; i++) {
    if (http.get(`${BASE}/health/ready`, { tags: { name: 'setup' } }).status === 200) {
      return;
    }
    sleep(1);
  }
  fail('PayFlow did not become ready within 60 s');
}

export function setup() {
  waitUntilReady();
  const adminEmail = __ENV.PAYFLOW_ADMIN_EMAIL;
  const adminPassword = __ENV.PAYFLOW_ADMIN_PASSWORD;
  if (!adminEmail || !adminPassword) {
    fail('Set PAYFLOW_ADMIN_EMAIL and PAYFLOW_ADMIN_PASSWORD (docker compose passes them from .env).');
  }
  const admin = signIn(adminEmail, adminPassword, __ENV.PAYFLOW_ADMIN_TOTP_SECRET);
  const run = `${Date.now().toString(36)}${Math.floor(Math.random() * 1e6).toString(36)}`;
  const keys = [];

  for (let m = 0; m < MERCHANTS; m++) {
    const tag = `${run}-${m}`;
    const ownerEmail = `owner-${tag}@load.test`;
    const created = dashboardPost(admin, '/api/merchants', {
      legalName: `Load Shop ${tag} (Pty) Ltd`,
      tradingName: `Load Shop ${tag}`,
      contactEmail: `ops-${tag}@load.test`,
      countryCode: 'ZA',
      currencies: [{ currency: 'ZAR', maxPaymentAmount: '50000.00', operatorRefundLimit: '500.00' }],
      adminEmail: ownerEmail,
      adminDisplayName: `Load Owner ${tag}`,
    }, 201).json();
    const merchantId = created.merchant.id;
    dashboardPost(admin, `/api/merchants/${merchantId}/status`, { status: 'Active', reason: null }, 200);

    // The invited owner sets a throwaway password and signs in.
    const ownerPassword = `load-${crypto.randomUUID()}`;
    const anonymous = new http.CookieJar();
    http.get(`${BASE}/api/auth/antiforgery`, { jar: anonymous, tags: { name: 'setup' } });
    dashboardPost(anonymous, '/api/auth/accept-invitation', {
      email: ownerEmail,
      token: created.adminInvitation.invitationToken,
      password: ownerPassword,
    }, 204);
    const owner = signIn(ownerEmail, ownerPassword);

    for (let k = 0; k < KEYS_PER_MERCHANT; k++) {
      keys.push(dashboardPost(owner, `/api/merchants/${merchantId}/api-keys`, { name: `load-${k}` }, 201).json().secret);
    }
  }

  return { keys };
}

export default function (data) {
  const n = exec.scenario.iterationInTest;
  const key = data.keys[n % data.keys.length];
  const idempotencyKey = crypto.randomUUID();
  const params = {
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${key}`, 'Idempotency-Key': idempotencyKey },
    tags: { name: 'create_payment' },
  };
  const body = JSON.stringify({
    amount: `${10 + (n % 990)}.${String(n % 100).padStart(2, '0')}`,
    currency: 'ZAR',
    paymentMethod: 'sim_card_success',
    merchantReference: `load-${exec.vu.idInTest}-${n}`,
  });

  const res = http.post(`${BASE}/v1/payments`, body, params);
  check(res, {
    created: (r) => r.status === 201,
    succeeded: (r) => r.status === 201 && r.json('status') === 'Succeeded',
  });

  if (REPLAY_EVERY > 0 && n % REPLAY_EVERY === 0 && res.status === 201) {
    const replay = http.post(`${BASE}/v1/payments`, body, { ...params, tags: { name: 'replay_payment' } });
    check(replay, {
      'replayed, not duplicated': (r) => r.status === 201 && r.headers['Idempotent-Replayed'] === 'true' && r.json('id') === res.json('id'),
    });
  }
}

import { APIResponse } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { resolve } from 'node:path';

/** The repository root, where docker-compose.yml lives. */
const repoRoot = resolve(__dirname, '../../..');

/** Runs `docker compose …` against the PayFlow stack and returns its stdout. */
export function compose(...args: string[]): string {
  return execFileSync('docker', ['compose', ...args], { cwd: repoRoot, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], timeout: 120_000 });
}

/**
 * Hard-kills the API container (SIGKILL: no graceful shutdown, in-flight requests and worker batches
 * die mid-way), keeps it down for a moment, then starts it again.
 */
export async function crashApi(downForMs = 3_000): Promise<void> {
  compose('kill', '-s', 'SIGKILL', 'api');
  await new Promise((r) => setTimeout(r, downForMs));
  compose('start', 'api');
}

/**
 * One read-only query against the stack's database, as tab-separated rows. Used only to assert on
 * state the public APIs do not expose in aggregate (ledger balance, event counts).
 */
export function sql(query: string): string[][] {
  const out = compose('exec', '-T', 'postgres', 'psql', '-U', 'payflow_migrator', '-d', 'payflow', '-v', 'ON_ERROR_STOP=1', '-At', '-F', '\t', '-c', query);
  return out.split(/\r?\n/).filter((l) => l.length > 0).map((l) => l.split('\t'));
}

/** A single scalar from {@link sql}. */
export function scalar(query: string): string {
  return sql(query)[0]?.[0] ?? '';
}

/** How many requests {@link untilAnswered} had to retry (proof the crashes hit live traffic). */
export const interrupted = { count: 0 };

/**
 * Sends a request until it gets a definitive answer, the way a merchant's server retries with the
 * same Idempotency-Key: connection errors, 502/503 while the API is down, and 409 "still in progress"
 * are all retried; anything else is returned.
 */
export async function untilAnswered(send: () => Promise<APIResponse>, timeoutMs = 120_000): Promise<APIResponse> {
  const deadline = Date.now() + timeoutMs;
  let last = 'no attempt';
  let retried = false;
  while (Date.now() < deadline) {
    if (last !== 'no attempt' && !retried) {
      retried = true;
      interrupted.count++;
    }
    try {
      const res = await send();
      if (![409, 502, 503, 504].includes(res.status())) {
        return res;
      }
      last = `HTTP ${res.status()}`;
    } catch (e) {
      last = String(e);
    }
    await new Promise((r) => setTimeout(r, 1_000));
  }
  throw new Error(`No definitive answer within ${timeoutMs} ms (last: ${last})`);
}

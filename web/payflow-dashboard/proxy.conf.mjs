// Development only: `npm run start:gateway` serves the app on http://localhost:4200 and forwards
// /api, /v1 and /health to the Compose HTTPS gateway, verifying its certificate against Caddy's
// local root CA (copy it once with: docker compose cp gateway:/data/caddy/pki/authorities/local/root.crt web/payflow-dashboard/.certs/caddy-root.crt).
// TLS verification is never turned off.
import { existsSync, readFileSync } from 'node:fs';
import { Agent } from 'node:https';

/** The gateway port from the repository's .env (PAYFLOW_HTTPS_PORT), so it matches docker compose. Nothing else is read. */
function gatewayPort() {
  const env = new URL('../../.env', import.meta.url);
  if (existsSync(env)) {
    const line = readFileSync(env, 'utf8').split(/\r?\n/).find((l) => l.startsWith('PAYFLOW_HTTPS_PORT='));
    if (line) {
      return line.slice('PAYFLOW_HTTPS_PORT='.length).trim();
    }
  }
  return '8443';
}

const target = process.env.PAYFLOW_GATEWAY ?? `https://localhost:${gatewayPort()}`;
const caFile = new URL('./.certs/caddy-root.crt', import.meta.url);
if (!existsSync(caFile)) {
  throw new Error('Missing .certs/caddy-root.crt: copy the gateway root CA first (see proxy.conf.mjs).');
}
const agent = new Agent({ ca: readFileSync(caFile) });
const route = { target, secure: true, changeOrigin: true, agent };

export default { '/api': route, '/v1': route, '/health': route };

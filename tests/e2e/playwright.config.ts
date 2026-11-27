import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end tests for the reviewer scenarios (docs/REQUIREMENTS.md §6), run against the Docker
 * Compose stack. The browser talks to the Angular dev server on http://localhost:4200, whose proxy
 * forwards /api, /v1 and /health to the HTTPS gateway and verifies its certificate against Caddy's
 * local CA (web/payflow-dashboard/proxy.conf.mjs). TLS verification is never turned off.
 *
 * Prerequisites: `docker compose up -d`, and the gateway CA copied to
 * web/payflow-dashboard/.certs/caddy-root.crt (see docs/TESTING.md).
 */
export default defineConfig({
  testDir: './scenarios',
  timeout: 90_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1, // scenarios share one platform admin and build on real, persistent data
  retries: process.env['CI'] ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
  use: {
    baseURL: process.env['PAYFLOW_E2E_BASE_URL'] ?? 'http://localhost:4200',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    ...devices['Desktop Chrome'],
  },
  webServer: process.env['PAYFLOW_E2E_BASE_URL']
    ? undefined
    : {
        command: 'npm --prefix ../../web/payflow-dashboard run start:gateway',
        url: 'http://localhost:4200/login',
        reuseExistingServer: true,
        timeout: 180_000,
      },
});

import { defineConfig } from '@playwright/test';
import base from './playwright.config';

/**
 * Chaos tests: they SIGKILL the Compose stack's api container, so they never run with the reviewer
 * scenarios. `npm run test:chaos`. Same prerequisites as the scenarios (docs/TESTING.md).
 */
export default defineConfig({
  ...base,
  testDir: './chaos',
  retries: 0,
});

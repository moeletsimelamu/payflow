import { defineConfig } from 'vitest/config';

// Loaded by the Angular unit-test builder (angular.json "runnerConfig") on top of its own setup.
export default defineConfig({
  test: {
    // One jsdom + Material + axe worker per core oversubscribes a busy machine, and under coverage
    // instrumentation single axe scans then took over a minute. Four workers matches a GitHub-hosted
    // runner and keeps each test's timing close to what it is alone.
    maxWorkers: 4,
    // Component tests boot Angular, Material and jsdom; under coverage on a busy runner even simple
    // ones can pass Vitest's 5-second default, and axe scans of the larger forms can take 30 s or
    // more. Same limit as src/testing/axe.ts, whichever is applied first. A slow test is not a
    // failing one.
    testTimeout: 60_000,
  },
});

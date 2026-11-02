import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: './tests/browser', testMatch: '**/*.spec.js', timeout: 45000, fullyParallel: false, workers: 1,
  // Assertions wait for Blazor Server renders and queued background jobs. On a busy CI runner
  // these take longer than the 5 s default; a passing assertion still returns as soon as it holds.
  expect: { timeout: 15000 },
  // Much of this suite only started running in CI, where slower runners surface UI timing races that
  // rarely appear locally. A test that passes on retry is still reported as flaky in the run's summary.
  retries: process.env.CI ? 2 : 0,
  globalSetup: './tests/browser/build-host.js',
  use: { headless: true, channel: process.env.LUMIBELLE_BROWSER_CHANNEL || 'msedge', screenshot: 'only-on-failure' }
});

import { defineConfig, devices } from '@playwright/test';

// The API serves the built Angular page and listens on two ports, so one
// browser test can be cross-origin: page on :5100, API calls to :5101.
export default defineConfig({
  testDir: './e2e',
  workers: 1,
  timeout: 30_000,
  reporter: 'list',
  use: { baseURL: 'http://localhost:5100', ...devices['Desktop Chrome'] },
  webServer: {
    command: 'dotnet run --project ../Api --no-launch-profile --urls "http://localhost:5100;http://localhost:5101"',
    url: 'http://localhost:5100/index.html',
    reuseExistingServer: false,
    timeout: 120_000,
    env: { PageOrigin: 'http://localhost:5100' },
  },
});

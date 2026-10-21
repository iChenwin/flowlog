import { defineConfig, devices } from '@playwright/test';
import { existsSync } from 'node:fs';
const installedChrome = 'C:/Program Files/Google/Chrome/Application/chrome.exe';
export default defineConfig({
  testDir: './e2e', fullyParallel: false, workers: 1,
  reporter: [['list']], use: {
    ...devices['Desktop Chrome'], baseURL: 'http://127.0.0.1:1420',
    launchOptions: process.platform === 'win32' && existsSync(installedChrome) ? { executablePath: installedChrome } : {},
    screenshot: 'only-on-failure', trace: 'retain-on-failure',
  },
  webServer: { command: 'npm run dev', url: 'http://127.0.0.1:1420', reuseExistingServer: !process.env.CI },
});

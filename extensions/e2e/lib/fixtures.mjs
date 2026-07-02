// Playwright fixture wiring the harness lifecycle into `test`. One harness instance per worker
// (not per test) — booting a fresh Cove container per test would make suites slow; instead each
// test is responsible for using its own uniquely-named seed data so tests don't collide.
//
// Usage in a test file:
//   import { test, expect } from '../../lib/fixtures.mjs';
//   test.use({ extension: { publishDir: '...', manifestPath: '...', uiBundlePath: '...' } });
//   test('...', async ({ page, baseUrl, api }) => { ... });
import { test as base, expect } from '@playwright/test';
import { startHarness } from './harness.mjs';

export const test = base.extend({
  extension: [undefined, { option: true }],

  // eslint-disable-next-line no-empty-pattern
  harness: [
    async ({}, use, workerInfo) => {
      const harness = await startHarness({ timeoutMs: 180_000 });
      await use(harness);
      await harness.stop();
    },
    { scope: 'worker' },
  ],

  baseUrl: async ({ harness, extension }, use) => {
    if (extension) {
      await harness.installExtension(extension);
    }
    await use(harness.baseUrl);
  },

  page: async ({ page, baseUrl }, use) => {
    await page.goto(baseUrl);
    await use(page);
  },

  api: async ({ baseUrl }, use) => {
    async function call(method, path, body) {
      const res = await fetch(`${baseUrl}${path}`, {
        method,
        headers: body ? { 'Content-Type': 'application/json' } : undefined,
        body: body ? JSON.stringify(body) : undefined,
      });
      const text = await res.text();
      let json;
      try {
        json = text ? JSON.parse(text) : undefined;
      } catch {
        json = undefined;
      }
      return { status: res.status, ok: res.ok, json, text };
    }
    await use({
      get: (path) => call('GET', path),
      post: (path, body) => call('POST', path, body),
      put: (path, body) => call('PUT', path, body),
      delete: (path) => call('DELETE', path),
    });
  },
});

export { expect };

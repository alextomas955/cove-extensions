// WhisparrSync-specific wiring on top of the shared @cove-extensions/e2e harness (which lives at
// tests/e2e/): pre-fills the `extension` fixture option with WhisparrSync's own build paths and
// re-exports the shared helpers so individual browser test files stay focused on behavior. Imports the
// harness BY PACKAGE NAME via npm workspaces — a second, separate @playwright/test install under this
// directory would break Playwright's module singleton, so this must never declare its own.
//
// This module serves the browser tier only. The non-browser correctness specs bring their own stack up
// through lib/setup.mjs and never import Playwright.
import { test as baseTest, expect } from '@cove-extensions/e2e';
import { seedVideo } from '@cove-extensions/e2e/seed-media';
import { pollJob, pollUntil } from '@cove-extensions/e2e/poll';
import { resolveExtensionPaths } from '@cove-extensions/e2e/resolve-extension';
import { seedCorpus } from './seed-fixtures.mjs';

export const EXTENSION_ID = 'com.alextomas955.whisparrsync';

export const WHISPARRSYNC_EXTENSION = resolveExtensionPaths(import.meta.url, {
  srcProject: 'WhisparrSync',
  uiProject: 'WhisparrSync.Ui',
});

export const test = baseTest.extend({
  extension: [WHISPARRSYNC_EXTENSION, { option: true }],
});

/**
 * Answer `/status` with a configuration the guard finds complete, so the controls a spec came to
 * exercise are enabled.
 *
 * A spec that drives Monitor / Unmonitor / Search but leaves this route live inherits whatever
 * quality profile the ambient instance happens to hold: with none picked, the guard dims exactly
 * those controls and the spec fails on a click that can never land. Stubbing it keeps the behaviour
 * under test independent of an instance setting the spec is not about. A spec that IS about the
 * guard must not call this — `config-guard.spec.mjs` deliberately lets the server answer.
 */
export async function routeUsableConfiguration(page) {
  await page.route(`**/extensions/${EXTENSION_ID}/status`, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        configured: true,
        hasApiKey: true,
        detectedVersion: '3.0.0',
        missingRequiredOptions: [],
      }),
    });
  });
}

/**
 * Answer `/status` as NOT configured, for a spec whose subject IS the unconfigured state.
 *
 * The mirror of {@link routeUsableConfiguration}, and needed for the same reason: extension options are
 * ONE blob per instance and the harness is worker-scoped, so a sibling spec that saves a connection
 * makes "no Whisparr configured" false for everything after it in that worker. A spec asserting the
 * unconfigured render must state that itself rather than inherit it — otherwise it passes alone and
 * fails in the suite, which is how it read as flaky.
 */
export async function routeUnconfigured(page) {
  await page.route(`**/extensions/${EXTENSION_ID}/status`, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        configured: false,
        hasApiKey: false,
        detectedVersion: null,
        missingRequiredOptions: ['baseUrl', 'apiKey'],
      }),
    });
  });
}

export { expect, seedVideo, pollJob, pollUntil, seedCorpus };

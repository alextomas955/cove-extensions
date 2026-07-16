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

export { expect, seedVideo, pollJob, pollUntil, seedCorpus };

// Renamer-specific wiring on top of the shared @cove-extensions/e2e harness (which lives at
// tests/e2e/): pre-fills the `extension` fixture option with Renamer's own build paths and re-exports
// the shared helpers so individual test files stay focused on behavior, not plumbing. Imports the
// harness BY PACKAGE NAME via npm workspaces — a second, separate @playwright/test install under this
// directory would break Playwright's module singleton, so this must never declare its own.
import { test as baseTest, expect } from '@cove-extensions/e2e';
import { seedVideo } from '@cove-extensions/e2e/seed-media';
import { pollJob, pollUntil } from '@cove-extensions/e2e/poll';
import { resolveExtensionPaths } from '@cove-extensions/e2e/resolve-extension';

export const RENAMER_EXTENSION = resolveExtensionPaths(import.meta.url, {
  srcProject: 'Renamer',
  uiProject: 'Renamer.Ui',
});

export const test = baseTest.extend({
  extension: [RENAMER_EXTENSION, { option: true }],
});

export { expect, seedVideo, pollJob, pollUntil };

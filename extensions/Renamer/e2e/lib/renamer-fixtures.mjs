// Renamer-specific wiring on top of the shared @cove-extensions/e2e harness (which lives at
// tests/e2e/): pre-fills the `extension` fixture option with Renamer's own build paths and re-exports
// the shared helpers so individual test files stay focused on behavior, not plumbing. Imports the
// harness BY PACKAGE NAME via npm workspaces — a second, separate @playwright/test install under this
// directory would break Playwright's module singleton, so this must never declare its own.
import { test as baseTest, isolatedHarnessFixture } from "@cove-extensions/e2e";
import { resolveExtensionPaths } from "@cove-extensions/e2e/resolve-extension";

export const RENAMER_EXTENSION = resolveExtensionPaths(import.meta.url, {
  srcProject: "Renamer",
});

export const test = baseTest.extend({
  extension: [RENAMER_EXTENSION, { option: true }],
});

/**
 * `test` with a per-TEST Cove instance carrying Renamer, exposed as the `isolatedHarness` fixture.
 *
 * For a spec that flips a global Renamer setting (AutoRenamerOnUpdate) or changes the extension's
 * installed state — both leak through the worker-scoped harness and silently change unrelated tests.
 * Five specs each declared this identically before it moved here.
 */
/**
 * Extends `test` above, NOT `baseTest`: a spec may use `isolatedHarness` for one test and the
 * worker-scoped `baseUrl`/`api` for its neighbours, and those install from the `extension` fixture
 * option. Extending the bare base would leave that option unset, so Renamer would silently not be
 * installed for exactly those tests.
 */
export const isolatedTest = test.extend({
  isolatedHarness: isolatedHarnessFixture(RENAMER_EXTENSION),
});

export { expect, createApiClient } from "@cove-extensions/e2e";
export { seedVideo } from "@cove-extensions/e2e/seed-media";
export { pollJob, pollUntil } from "@cove-extensions/e2e/poll";

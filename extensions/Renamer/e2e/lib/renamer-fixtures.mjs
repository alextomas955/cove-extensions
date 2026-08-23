// Renamer's wiring on top of the shared harness at tests/e2e/: pre-fills the `extension` fixture
// option with Renamer's own build paths, and re-exports the shared helpers.
//
// The harness is imported BY PACKAGE NAME through npm workspaces. A second @playwright/test install
// under this directory would break Playwright's module singleton, so this must never declare one.
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
 * For a spec that flips a global Renamer setting or changes the extension's installed state, both of
 * which leak through the worker-scoped harness.
 *
 * Extends `test` above and NOT `baseTest`: a spec may use `isolatedHarness` for one test and the
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

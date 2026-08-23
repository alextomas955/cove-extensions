// Renamer's wiring on top of the shared harness at tests/e2e/: pre-fills the `extension` fixture
// option with Renamer's own build paths, and re-exports the shared helpers.
//
// The harness is imported BY PACKAGE NAME through npm workspaces. A second @playwright/test install
// under this directory would break Playwright's module singleton, so this must never declare one.
import { test as baseTest } from "@cove-extensions/e2e";
import { resolveExtensionPaths } from "@cove-extensions/e2e/resolve-extension";

export const RENAMER_EXTENSION = resolveExtensionPaths(import.meta.url, {
  srcProject: "Renamer",
});

export const test = baseTest.extend({
  extension: [RENAMER_EXTENSION, { option: true }],
});

export { expect } from "@cove-extensions/e2e";
export { seedVideo } from "@cove-extensions/e2e/seed-media";
export { pollJob, pollUntil } from "@cove-extensions/e2e/poll";

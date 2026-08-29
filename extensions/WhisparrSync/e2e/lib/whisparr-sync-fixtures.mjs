// Whisparr Sync's wiring on top of the shared harness at tests/e2e/: pre-fills the `extension`
// fixture option with this extension's own build paths, and re-exports the shared helpers.
//
// The harness is imported BY PACKAGE NAME through npm workspaces. A second @playwright/test install
// under this directory would break Playwright's module singleton, so this must never declare one.
//
// This file must stay at e2e/lib/: resolveExtensionPaths walks a fixed number of parents from the
// caller's own module URL, so moving it silently relocates every path it derives.
import { test as baseTest } from "@cove-extensions/e2e";
import { resolveExtensionPaths } from "@cove-extensions/e2e/resolve-extension";

export const WHISPARR_SYNC_EXTENSION = resolveExtensionPaths(import.meta.url, {
  srcProject: "WhisparrSync",
});

export const test = baseTest.extend({
  extension: [WHISPARR_SYNC_EXTENSION, { option: true }],
});

export { expect } from "@cove-extensions/e2e";

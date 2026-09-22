// Renamer's wiring on top of the shared harness at tests/e2e/: pre-fills the `extension` fixture
// option with Renamer's own build paths, and re-exports the shared helpers.
//
// The harness is imported by package name through npm workspaces. A second @playwright/test install
// under this directory would break Playwright's module singleton, so this must never declare one.
import { test as baseTest, expect } from "@cove-extensions/e2e";
import { resolveExtensionPaths } from "@cove-extensions/e2e/resolve-extension";

const RENAMER_EXTENSION_ID = "com.alextomas955.renamer";

export const RENAMER_EXTENSION = resolveExtensionPaths(import.meta.url, {
  srcProject: "Renamer",
});

export const test = baseTest.extend({
  // Worker-scoped to match the option the shared fixtures declare. An override at test scope would
  // not satisfy the worker-scoped `harness` fixture that reads it, and the runner refuses the mix.
  extension: [RENAMER_EXTENSION, { scope: "worker", option: true }],

  // A test that saves a setting on the worker's shared instance would change what every later test
  // in that worker renders with, so the stored document is put back afterwards. The host has no
  // delete for extension data; an empty document loads as the defaults.
  restoredOptions: [
    async ({ api }, use) => {
      const route = `/api/extensions/${RENAMER_EXTENSION_ID}/data`;
      const before = (await api.get(route)).json?.options ?? "{}";
      await use();
      const restore = await api.put(`${route}/options`, before);
      expect(restore.ok, `restoring the stored options answered ${restore.status}`).toBe(true);
    },
    { scope: "test" },
  ],
});

export { expect };
export { seedVideo } from "@cove-extensions/e2e/seed-media";
export { pollUntil } from "@cove-extensions/e2e/poll";

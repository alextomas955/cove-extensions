// Renamer's wiring on top of the shared harness at tests/e2e/: pre-fills the `extension` fixture
// option with Renamer's own build paths, and re-exports the shared helpers.
//
// The harness is imported by package name through npm workspaces. A second @playwright/test install
// under this directory would break Playwright's module singleton, so this must never declare one.
import { test as baseTest, expect, createApiClient } from "@cove-extensions/e2e";
import { resolveExtensionPaths } from "@cove-extensions/e2e/resolve-extension";

export const EXTENSION_ID = "com.alextomas955.renamer";
export const ROUTE = `/api/extensions/${EXTENSION_ID}`;

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
      const route = `${ROUTE}/data`;
      const before = (await api.get(route)).json?.options ?? "{}";
      await use();
      const restore = await api.put(`${route}/options`, before);
      expect(restore.ok, `restoring the stored options answered ${restore.status}`).toBe(true);
    },
    { scope: "test" },
  ],
});

export { expect };

/**
 * An API client for a harness that outlives a restart: both the address and the token are read on
 * every call, because a restart re-mints the token and may republish the port.
 */
export function clientFor(harness) {
  return createApiClient(
    () => harness.baseUrl,
    () => harness.token,
  );
}

/**
 * The extension's stored options blob, parsed, or undefined when the key is absent. The blob is the
 * PascalCase spelling of the C# record, not the camelCase of the wire document.
 */
export async function storedOptions(api) {
  const all = await api.get(`${ROUTE}/data`);
  expect(all.ok, `reading the extension store answered ${all.status}: ${all.text}`).toBe(true);
  const blob = (all.json ?? {}).options;
  return blob ? JSON.parse(blob) : undefined;
}

/**
 * Runs one SQL statement in the harness's database container and returns its unaligned output.
 *
 * The statement travels as an environment variable, so it can hold quotes with no escaping rule, and
 * the credentials come from the container's own environment rather than a copy of the compose file.
 */
export async function queryDb(harness, sql) {
  const result = await harness.execDb(
    ["sh", "-c", 'psql -v ON_ERROR_STOP=1 -tAX -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "$SQL"'],
    { env: { SQL: sql } },
  );
  expect(result.exitCode, `psql failed for [${sql}]: ${result.output}`).toBe(0);
  return result.output.trim();
}
export { seedVideo } from "@cove-extensions/e2e/seed-media";
export { pollUntil } from "@cove-extensions/e2e/poll";

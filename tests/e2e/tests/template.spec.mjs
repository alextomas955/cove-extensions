// Copyable starting point for a new extension's E2E suite. Copy this file into your extension's own
// test directory (extensions/<YourExt>/e2e/tests/), then change the `srcProject` string below to
// your extension's .NET project name and replace the example tests with your own.
//
// This template lives inside the shared harness, so it imports it with relative paths. In your own
// extension import it by package name instead - `@cove-extensions/e2e` and
// `@cove-extensions/e2e/resolve-extension` - which npm workspaces resolves. See
// extensions/Renamer/e2e/lib/renamer-fixtures.mjs, and the "Authoring E2E tests" docs page
// (https://alextomas955.github.io/cove-extensions/contributing/authoring-e2e).
//
// It does not run where it sits: playwright.config.mjs derives a project from each catalog entry's
// e2ePath, and this directory is none. Once copied, run it with the rest of your suite:
// cd tests/e2e && npm test -- --project=<your e2eProject>
import { test, expect } from "../lib/fixtures.mjs";
import { resolveExtensionPaths } from "../lib/resolve-extension.mjs";

// resolveExtensionPaths derives your extension's build outputs from this file's own location, so a
// copy resolves relative to itself. The UI bundle needs no argument: what a package ships is
// declared in your extension's catalog.json entry, and the harness installs that declared set.
test.use({
  extension: resolveExtensionPaths(import.meta.url, {
    srcProject: "Renamer", // → src/<srcProject>/extension.json  (change to your .NET project name)
  }),
});

test("extension installs and reports enabled via the API", async ({ api }) => {
  const { ok, json } = await api.get("/api/extensions");
  expect(ok).toBe(true);
  const installed = json.find((e) => e.id === "com.alextomas955.renamer");
  expect(installed?.enabled).toBe(true);
});

test("Cove home page loads in the browser against the containerized instance", async ({ page }) => {
  await expect(page).toHaveTitle(/Cove/i);
});

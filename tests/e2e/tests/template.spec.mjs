// Copyable starting point for a new extension's E2E suite. Copy this file into your extension's own
// test directory (extensions/<YourExt>/e2e/tests/), then change the two `*Project` strings below to
// your extension's .NET + UI project names and replace the example tests with your own.
//
// NOTE ON IMPORTS: this template lives INSIDE the shared harness (tests/e2e/), so it imports the
// harness with relative paths (`../lib/...`). When you copy it into your extension, change those to
// the package name — `@cove-extensions/e2e` and `@cove-extensions/e2e/resolve-extension` — so there
// is no `../../../` path archaeology (npm workspaces resolves the harness by name). See
// extensions/Renamer/e2e/lib/renamer-fixtures.mjs for the real-world shape and docs/AUTHORING-E2E.md
// for the full 3-step add-a-suite guide.
//
// Run: cd tests/e2e && npm test -- template.spec.mjs
import { test, expect } from '../lib/fixtures.mjs';
import { resolveExtensionPaths } from '../lib/resolve-extension.mjs';

// resolveExtensionPaths derives your extension's build outputs from THIS file's own location — no
// hand-rolled repo-root math. When copied into your extension it resolves relative to the copy.
test.use({
  extension: resolveExtensionPaths(import.meta.url, {
    srcProject: 'Renamer', // → src/<srcProject>/extension.json  (change to your .NET project name)
    uiProject: 'Renamer.Ui', // → src/<uiProject>/dist/index.mjs  (change to your UI project name)
  }),
});

test('extension installs and reports enabled via the API', async ({ api }) => {
  const { ok, json } = await api.get('/api/extensions');
  expect(ok).toBe(true);
  const installed = json.find((e) => e.id === 'com.alextomas955.renamer');
  expect(installed?.enabled).toBe(true);
});

test('Cove home page loads in the browser against the containerized instance', async ({ page }) => {
  await expect(page).toHaveTitle(/Cove/i);
});

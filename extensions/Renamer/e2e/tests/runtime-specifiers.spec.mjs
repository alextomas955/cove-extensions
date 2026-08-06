// Drives the two halves of the canonical-specifier contract against a real released host: that the
// host's import map SERVES "@cove/runtime/api" and "@cove/runtime/components", and that the bundle
// the host actually SERVES imports a named symbol from each of them.
//
// The rest of this suite already goes red if a canonical specifier fails to resolve — an ESM
// resolution failure kills the whole bundle, the settings tab never appears, and core-paths'
// pageerror assertion fires. But that is an implicit pass: it names nothing, so it cannot tell a
// missing specifier apart from any other reason the panel did not render, and it would keep passing
// if the imports quietly disappeared from the bundle. These two tests state what they examined and
// name what was missing, which is the bar a check here has to clear.
//
// Test B deliberately reads the INSTALLED artifact over HTTP rather than the local dist/, so it
// covers the build → publish → install → serve path rather than re-asserting a fact about a file
// the build just wrote.
import { test, expect } from '../lib/renamer-fixtures.mjs';
import { RenamerSettingsPage } from '../lib/pages/renamer-settings-page.mjs';

const EXTENSION_ID = 'com.alextomas955.renamer';

// A NAMED import binding, never a bare side-effect `import "@cove/runtime/api";` — an external with
// no live binding degrades to exactly that, which proves the specifier resolves but proves nothing
// about the named export. `\s*` accepts both the spaced form this build emits and a minified
// spaceless one. Byte-identical to the assertion the build-time check applies to dist/index.mjs.
const NAMED_EXTENSION_FETCH_IMPORT =
  /import\s*\{[^}]*\bextensionFetch\b[^}]*\}\s*from\s*"@cove\/runtime\/api"/;

// The components half, identical in shape. It could not be asserted until a consumer existed: the
// module was declared but unused, and a specifier nothing imports is served without ever being
// linked. The settings render below is what makes this more than a string match — a named export
// missing from a live ESM module is a LINK-time failure, so the module never evaluates and the
// whole bundle dies with it, taking every extension's UI on the page down together.
const NAMED_MULTI_SELECTOR_IMPORT =
  /import\s*\{[^}]*\bEntityReferenceMultiSelector\b[^}]*\}\s*from\s*"@cove\/runtime\/components"/;

test('the host import map serves both canonical @cove/runtime specifiers', async ({ page }) => {
  const imports = await page.evaluate(() => {
    const el = document.querySelector('script[type="importmap"]');
    if (!el?.textContent) return null;
    try {
      return Object.keys(JSON.parse(el.textContent).imports ?? {});
    } catch {
      return null;
    }
  });

  expect(
    imports,
    'no readable <script type="importmap"> in the host document — this check inspected nothing',
  ).not.toBeNull();
  expect(
    imports.length,
    'the host import map is empty — this check inspected nothing',
  ).toBeGreaterThan(0);

  // Asserted one key at a time so a failure names the specifier the host did not serve, rather than
  // printing a whole-set mismatch the reader has to diff by eye.
  expect(
    imports,
    `host import map has no "@cove/runtime/api" key. It serves: ${imports.join(', ')}`,
  ).toContain('@cove/runtime/api');
  expect(
    imports,
    `host import map has no "@cove/runtime/components" key. It serves: ${imports.join(', ')}`,
  ).toContain('@cove/runtime/components');
});

test('the bundle the host serves imports extensionFetch by name, and the settings surface loads clean', async ({
  page,
  baseUrl,
  api,
}) => {
  // Ask the host where it serves the bundle instead of hard-coding a route, so this keeps working if
  // the asset path or its cache-busting query changes.
  const manifest = await api.get('/api/extensions/manifest');
  expect(
    manifest.ok,
    `GET /api/extensions/manifest returned ${manifest.status}`,
  ).toBe(true);

  const bundles = manifest.json?.extensionBundles ?? [];
  const record = bundles.find((b) => b.extensionId === EXTENSION_ID);
  expect(
    record,
    `the host manifest lists no bundle for ${EXTENSION_ID}. It lists: ${bundles.map((b) => b.extensionId).join(', ') || '(none)'}`,
  ).toBeTruthy();

  const bundleUrl = record.jsBundleUrl;
  expect(
    bundleUrl,
    `the host manifest entry for ${EXTENSION_ID} carries no jsBundleUrl, so there is no served artifact to inspect`,
  ).toBeTruthy();

  const served = await api.get(bundleUrl);
  expect(served.ok, `GET ${bundleUrl} returned ${served.status}`).toBe(true);
  expect(
    served.text.length,
    `the bundle served at ${bundleUrl} is empty — an empty body would trivially "contain nothing"`,
  ).toBeGreaterThan(0);

  const match = served.text.match(NAMED_EXTENSION_FETCH_IMPORT);
  const runtimeImports =
    served.text.match(/import[^;]*?from\s*"@cove\/runtime\/[^"]+"/g) ?? [];
  expect(
    match,
    `the bundle served at ${bundleUrl} has no NAMED import binding extensionFetch from "@cove/runtime/api" ` +
      `(a bare side-effect import would not satisfy this). @cove/runtime imports actually present: ` +
      `${runtimeImports.length ? runtimeImports.join(' | ') : '(none)'}`,
  ).not.toBeNull();
  expect(
    served.text.match(NAMED_MULTI_SELECTOR_IMPORT),
    `the bundle served at ${bundleUrl} has no NAMED import binding EntityReferenceMultiSelector from ` +
      `"@cove/runtime/components". @cove/runtime imports actually present: ` +
      `${runtimeImports.length ? runtimeImports.join(' | ') : '(none)'}`,
  ).not.toBeNull();

  // Having proven the served artifact carries the import, prove the host resolves it: an
  // unresolvable specifier kills the whole bundle before the panel can render.
  const errors = [];
  page.on('pageerror', (err) => errors.push(err.message));

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  await expect(settings.filenameTemplateInput).toBeVisible({ timeout: 15_000 });

  expect(
    errors,
    `the settings surface raised page errors, so a served specifier did not resolve: ${errors.join('; ')}`,
  ).toEqual([]);
});

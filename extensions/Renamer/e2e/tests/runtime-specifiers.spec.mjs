// Drives the canonical-specifier contract against a real released host: the bundle the host actually
// SERVES imports a named symbol from each of "@cove/runtime/api" and "@cove/runtime/components".
//
// The rest of this suite does NOT reliably go red when a canonical specifier stops resolving, and
// that is the reason this test exists rather than a footnote to it. Measured against a released host:
// a bundle that fails to LINK — importing a named export the host no longer provides — takes the
// panel down with ZERO page errors and zero console errors, so a pageerror gate is silent for exactly
// that failure. What does catch it is every assertion that waits for the settings panel to render,
// this test's own included. What none of them can do is NAME the specifier that went missing, or
// notice the imports quietly disappearing from the bundle at all, because a bundle importing nothing
// links perfectly well.
//
// Nothing else in the repo closes that gap either: `createExtensionViteConfig.ts` declares the two
// specifiers as rollup EXTERNALS, which makes them external and says nothing about the built bundle
// importing them BY NAME. That list is also a hand-mirror of Cove's own — a cross-system contract
// nothing on the server side checks — so this is the only drift detection it has.
//
// It reads the INSTALLED artifact over HTTP rather than the local dist/, so it covers the
// build → publish → install → serve path rather than re-asserting a fact about a file the build just
// wrote.
import { test, expect } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

const EXTENSION_ID = "com.alextomas955.renamer";

// A NAMED import binding, never a bare side-effect `import "@cove/runtime/api";` — an external with
// no live binding degrades to exactly that, which proves the specifier resolves but proves nothing
// about the named export. `\s*` accepts both the spaced form this build emits and a minified
// spaceless one.
const NAMED_EXTENSION_FETCH_IMPORT =
  /import\s*\{[^}]*\bextensionFetch\b[^}]*\}\s*from\s*"@cove\/runtime\/api"/;

// The components half, identical in shape. It could not be asserted until a consumer existed: the
// module was declared but unused, and a specifier nothing imports is served without ever being
// linked. The settings render below is what makes this more than a string match — a named export
// missing from a live ESM module is a LINK-time failure, so the module never evaluates and the
// whole bundle dies with it, taking every extension's UI on the page down together.
const NAMED_MULTI_SELECTOR_IMPORT =
  /import\s*\{[^}]*\bEntityReferenceMultiSelector\b[^}]*\}\s*from\s*"@cove\/runtime\/components"/;

test("the bundle the host serves imports extensionFetch by name, and the settings surface loads clean", async ({
  page,
  baseUrl,
  api,
}) => {
  // Ask the host where it serves the bundle instead of hard-coding a route, so this keeps working if
  // the asset path or its cache-busting query changes.
  const manifest = await api.get("/api/extensions/manifest");
  expect(manifest.ok, `GET /api/extensions/manifest returned ${manifest.status}`).toBe(true);

  const bundles = manifest.json?.extensionBundles ?? [];
  const record = bundles.find((b) => b.extensionId === EXTENSION_ID);
  expect(
    record,
    `the host manifest lists no bundle for ${EXTENSION_ID}. It lists: ${bundles.map((b) => b.extensionId).join(", ") || "(none)"}`,
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
  const runtimeImports = served.text.match(/import[^;]*?from\s*"@cove\/runtime\/[^"]+"/g) ?? [];
  expect(
    match,
    `the bundle served at ${bundleUrl} has no NAMED import binding extensionFetch from "@cove/runtime/api" ` +
      `(a bare side-effect import would not satisfy this). @cove/runtime imports actually present: ` +
      `${runtimeImports.length ? runtimeImports.join(" | ") : "(none)"}`,
  ).not.toBeNull();
  expect(
    served.text.match(NAMED_MULTI_SELECTOR_IMPORT),
    `the bundle served at ${bundleUrl} has no NAMED import binding EntityReferenceMultiSelector from ` +
      `"@cove/runtime/components". @cove/runtime imports actually present: ` +
      `${runtimeImports.length ? runtimeImports.join(" | ") : "(none)"}`,
  ).not.toBeNull();

  // Having proven the served artifact carries the imports, prove the host resolves them. The
  // VISIBILITY assertion is what does that work: an unresolvable specifier kills the bundle before
  // the panel can render, and it does so silently. The pageerror check below is a second, weaker net
  // for anything that does throw — worth keeping, but it is not what would catch a missing specifier.
  const errors = [];
  page.on("pageerror", (err) => errors.push(err.message));

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  await expect(
    settings.filenameTemplateInput,
    "the settings panel never rendered, which is what a specifier the host cannot resolve looks like",
  ).toBeVisible({ timeout: 15_000 });

  expect(errors, `the settings surface raised page errors: ${errors.join("; ")}`).toEqual([]);
});

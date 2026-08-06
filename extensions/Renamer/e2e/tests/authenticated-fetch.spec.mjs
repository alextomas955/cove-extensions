// The one spec that runs against an AUTHENTICATION-ENABLED Cove, and the only place this repo can
// tell an extension request that carries its own credential apart from one that does not. Under the
// suite's normal `COVE__Auth__Enabled=false` default every request resolves to a bypass principal
// whatever headers it carries, so neither a header assertion nor a 200 distinguishes fixed from
// unfixed there — the check would be unfalsifiable. Flipping auth is instance-global, hence the
// per-test harness rather than the worker-shared one. It stays in the default run regardless: it
// provisions its own instance and shares no state, and a spec CI does not run guards nothing.
//
// WHAT MAKES IT FALSIFIABLE, and it is not what it looks like. A logged-in browser holds an
// `cove_access_token` cookie, and the host's principal middleware falls back to that cookie whenever
// a request carries no Authorization header — so an extension calling plain `fetch` same-origin is
// authenticated by ambient authority and answers 200 either way. Measured: this spec passed against
// the unmigrated bundle until the cookie was taken out of the picture. That cookie is also what
// delivers the extension's own UI bundle (`/api/extensions/assets/...` requires ExtensionsRead and a
// module import cannot carry a bearer), so it must be present for the panel to mount at all.
//
// Hence the shape below: the panel mounts and reads WITH the cookie, then the cookie is dropped and
// the write is exercised on the already-mounted panel. A logged-in session whose access cookie has
// lapsed is ordinary — the cookie expires with the access token, in minutes, while the refresh
// session lasts days — and in that state the only credential left is the bearer the app holds, which
// an extension can reach only through the host's authenticated fetch.
//
// Built on `@playwright/test` rather than the shared fixtures: taking their `page` would pull in the
// worker-scoped auth-off harness and boot a second Cove instance this spec never talks to. The
// setup-wizard pre-seed below is the one thing it borrows, because the no-library-path gate is
// orthogonal to authentication and still fires here.
import { test as base, expect } from '@playwright/test';
import { loginThroughUi } from '@cove-extensions/e2e';
import { startHarness } from '@cove-extensions/e2e/harness';
import { RENAMER_EXTENSION } from '../lib/renamer-fixtures.mjs';
import { RenamerSettingsPage } from '../lib/pages/renamer-settings-page.mjs';

const ACCESS_COOKIE = 'cove_access_token';

const test = base.extend({
  authHarness: [
    async ({}, use) => {
      const harness = await startHarness({ env: { COVE_E2E_AUTH_ENABLED: 'true' } });
      await harness.bootstrapOwner();
      // The install reads the id out of the manifest, which is where it is defined; a copy here
      // would go stale silently, because the panel would follow the manifest to the new route while
      // every response predicate below kept matching the old one — and a predicate that matches
      // nothing fails as a bare 30s timeout, naming neither the id nor the mismatch.
      const { id } = await harness.installExtension(RENAMER_EXTENSION);
      await use({ harness, extensionId: id });
      await harness.stop();
    },
    { scope: 'test' },
  ],
});

/** Writes the stored options blob out-of-band, as the host's own route wants it (double-encoded). */
async function putStoredOptions(harness, dataPathname, payload) {
  const res = await fetch(`${harness.baseUrl}${dataPathname}/options`, {
    method: 'PUT',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${harness.token}`,
    },
    body: JSON.stringify(JSON.stringify(payload)),
  });
  expect(res.status, `seeding the options blob answered ${res.status}`).toBe(200);
}

/**
 * Drops the access cookie and proves it is gone.
 *
 * The proof is the whole spec: with that cookie in place the host's principal middleware
 * authenticates an extension's plain same-origin `fetch` by ambient authority, so every assertion
 * below passes whether or not the request carries a credential of its own — which is the measured
 * historical behavior of this exact test, not a hypothesis. An unasserted `clearCookies` can stop
 * clearing (its filter semantics are Playwright's to change) or clear a cookie the host has since
 * started setting on a second path, and nothing would say so.
 */
async function dropAmbientAuthority(page) {
  await page.context().clearCookies({ name: ACCESS_COOKIE });
  expect(
    (await page.context().cookies()).map((c) => c.name),
    `the ${ACCESS_COOKIE} cookie survived clearCookies — ambient authority is still in play, so a 200 below would prove nothing`
  ).not.toContain(ACCESS_COOKIE);
}

/**
 * Clicks Save and waits for that save's own write to answer, returning its status. Gating on the
 * "Unsaved changes" indicator alone is not enough: it hides on the panel's local state, so a second
 * save can be driven before the first write has landed and the two arrive out of order.
 *
 * Drops the cookie again first rather than trusting the drop before the previous save: a 401 on any
 * request in between drives the app's own refresh, and the host re-issues the access cookie on that
 * response — handing the remaining writes back the ambient authority this spec exists to rule out.
 */
async function saveAndAwaitWrite(page, settings, dataPathname) {
  await dropAmbientAuthority(page);
  const write = page.waitForResponse(
    (res) => new URL(res.url()).pathname.startsWith(dataPathname) && res.request().method() === 'PUT',
    { timeout: 30_000 }
  );
  await settings.saveChangesButton.click();
  const status = (await write).status();
  await expect(settings.unsavedChangesIndicator).toBeHidden({ timeout: 10_000 });
  return status;
}

/** Reads the stored options blob back as the raw string the host holds. */
async function readStoredOptions(harness, dataPathname) {
  const res = await fetch(`${harness.baseUrl}${dataPathname}`, {
    headers: { Authorization: `Bearer ${harness.token}` },
  });
  expect(res.status, `reading the options blob answered ${res.status}`).toBe(200);
  return (await res.json()).options;
}

test('the settings panel reads and writes its options through an authenticated request', async ({
  page,
  authHarness,
}) => {
  const { harness, extensionId } = authHarness;
  const dataPathname = `/api/extensions/${extensionId}/data`;

  const seededTemplate = `$title [authenticated-${Date.now()}]`;
  await putStoredOptions(harness, dataPathname, { FilenameTemplate: seededTemplate });

  await page.addInitScript(() => {
    sessionStorage.setItem('cove-setup-dismissed', 'true');
  });
  await page.goto(harness.baseUrl);
  await loginThroughUi(page);

  const settings = new RenamerSettingsPage(page, harness.baseUrl);
  const firstRead = page.waitForResponse(
    (res) => new URL(res.url()).pathname === dataPathname && res.request().method() === 'GET',
    { timeout: 30_000 }
  );
  await settings.goto();

  const firstReadStatus = (await firstRead).status();
  expect(firstReadStatus, `the bundle's GET ${dataPathname} answered ${firstReadStatus}`).toBe(200);

  // The seeded value can only reach the input if that read returned 200, parsed, and flowed through
  // the panel; a failed read leaves the panel on its load-error path showing built-in defaults.
  await expect(settings.filenameTemplateInput).toHaveValue(seededTemplate, { timeout: 30_000 });

  // Load twice: same instance, same stored blob, same rendered options.
  await settings.goto();
  await expect(settings.filenameTemplateInput).toHaveValue(seededTemplate, { timeout: 30_000 });

  // Ambient authority gone, session intact — from here only a request carrying the app's own bearer
  // can reach the host store. The panel is already mounted, so its bundle (itself cookie-delivered)
  // is not refetched and this isolates the extension's request from how it was delivered.
  await dropAmbientAuthority(page);

  const editedTemplate = `${seededTemplate} edited`;
  await settings.setFilenameTemplate(editedTemplate);
  const firstWriteStatus = await saveAndAwaitWrite(page, settings, dataPathname);
  expect(
    firstWriteStatus,
    `the panel's PUT to ${dataPathname} answered ${firstWriteStatus} with no access cookie in play — an extension request that carries no credential of its own cannot write to the host store`
  ).toBe(200);

  const afterFirstSave = await readStoredOptions(harness, dataPathname);
  expect(JSON.parse(afterFirstSave).FilenameTemplate).toBe(editedTemplate);

  // Save the identical payload again after a round trip through another value. A save is a full
  // replacement, so a second write of the same payload that differs byte-for-byte means something
  // is being carried in that the panel did not read out.
  await settings.setFilenameTemplate(seededTemplate);
  expect(await saveAndAwaitWrite(page, settings, dataPathname)).toBe(200);
  await settings.setFilenameTemplate(editedTemplate);
  expect(await saveAndAwaitWrite(page, settings, dataPathname)).toBe(200);

  expect(
    await readStoredOptions(harness, dataPathname),
    'the same payload saved twice produced two different stored blobs'
  ).toBe(afterFirstSave);
});

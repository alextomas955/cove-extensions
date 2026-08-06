// The one call site in this extension that uses the host's authenticated `extensionFetch`
// (StudioMap.tsx) ends in a bare `.catch()`: a broken fetch renders the raw studio id instead of its
// name and throws nothing, logs nothing, and fails no other gate. That silence is right for a user —
// the label is a readability aid, not load-bearing — and wrong for a suite, so the only way to cover
// this path is from outside the component, against observables the catch cannot swallow.
//
// Two independent ones, so a single mis-assertion cannot fake a pass: the committed rule's rendered
// label, which can only read as the studio's NAME if the fetch resolved, was authenticated, returned
// 200 and its JSON parsed; and the HTTP status of the request itself. The editor also sits behind an
// off-by-default toggle, which is why nothing reached it before.
//
// The assertion runs on a FRESH page load with the studio picker untouched. The picker hits the same
// list-studios endpoint, but through the SDK's `request()` — a different code path — so a 200 caught
// while it is in play would be attributable to either. On a fresh load nothing else calls this
// endpoint, so the captured response is extensionFetch's.
import { test, expect } from '../lib/renamer-fixtures.mjs';
import { RenamerSettingsPage } from '../lib/pages/renamer-settings-page.mjs';

const EXTENSION_ID = 'com.alextomas955.renamer';
const LIST_STUDIOS_PATHNAME = `/api/extensions/${EXTENSION_ID}/list-studios`;

// Pathname-exact, never a substring: a mutation that points the fetch at a neighbouring route
// (".../list-studios-nope") must NOT satisfy this, or the check would green-light the very defect it
// exists to catch.
function isListStudiosResponse(response) {
  return new URL(response.url()).pathname === LIST_STUDIOS_PATHNAME;
}

test('a committed per-studio rule resolves to its studio name, proving extensionFetch reached the host', async ({
  page,
  baseUrl,
  api,
}) => {
  const errors = [];
  page.on('pageerror', (err) => errors.push(err.message));

  // Unique per run: the per-worker Cove instance is shared, and the picker is driven by typing this
  // name, so it has to narrow to exactly one row.
  const studioName = `E2E StudioMap Studio ${Date.now()}`;
  const created = await api.post('/api/studios', { name: studioName });
  expect(
    created.ok,
    `POST /api/studios returned ${created.status} — the library has no studio to key a rule on, so this test would prove nothing: ${created.text}`,
  ).toBe(true);
  const studioId = created.json.id;

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  await expect(settings.filenameTemplateInput).toBeVisible({ timeout: 15_000 });

  const studioCard = page
    .getByRole('heading', { name: 'Per-studio destinations', exact: true })
    .locator('xpath=ancestor::section[1]');
  const enableSwitch = studioCard.getByRole('switch');

  await expect(
    enableSwitch,
    'the "Per-studio destinations" toggle is not off on a fresh instance, so this run is not exercising the default state the editor hides behind',
  ).toHaveAttribute('aria-checked', 'false');
  await enableSwitch.click();

  // Commit the rule the way a user does: pick the studio in the searchable picker, type a
  // destination, add the row, save.
  const picker = studioCard.getByPlaceholder('Search studios…');
  await picker.click();
  await picker.fill(studioName);
  await studioCard.getByRole('button', { name: studioName, exact: true }).click();
  await studioCard.getByPlaceholder('Destination root').fill('/data/studio-dest');
  await studioCard.getByRole('button', { name: 'Add studio rule' }).click();
  await settings.save();

  const listStudios = page
    .waitForResponse(isListStudiosResponse, { timeout: 20_000 })
    .catch(() => null);
  await page.reload();
  const response = await listStudios;

  expect(
    response,
    `nothing requested ${LIST_STUDIOS_PATHNAME} after the saved rule re-mounted the editor — extensionFetch never reached the host, and the swallowed failure would leave the raw id showing`,
  ).not.toBeNull();
  expect(
    response.status(),
    `${LIST_STUDIOS_PATHNAME} answered extensionFetch with ${response.status()}, not 200 — the component discards a non-ok response and renders the raw id`,
  ).toBe(200);

  const ruleRow = studioCard
    .getByRole('button', { name: `Remove ${studioId}`, exact: true })
    .locator('xpath=..');
  await expect(
    ruleRow,
    `the committed rule never resolved to "${studioName}". resolveStudioLabel falls back to "#${studioId} (missing)" whenever the fetched list is empty, which is exactly how a silently-failed extensionFetch renders`,
  ).toContainText(studioName, { timeout: 15_000 });

  expect(
    errors,
    `the studio-map editor raised page errors: ${errors.join('; ')}`,
  ).toEqual([]);

  // Clear the rule and re-hide the editor so a saved studio destination does not leak into a sibling
  // test sharing this worker's Cove instance (mirrors core-paths.spec.mjs's cleanup).
  await studioCard.getByRole('button', { name: `Remove ${studioId}`, exact: true }).click();
  await enableSwitch.click();
  await settings.save();
});

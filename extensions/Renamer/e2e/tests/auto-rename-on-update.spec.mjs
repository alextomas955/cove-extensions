// Verifies Renamer's AutoRenamerOnUpdate hook end-to-end through the real UI: enabling "Auto-
// rename on update" in the settings panel, then editing a video's title via its real Edit tab,
// must rename the file automatically — with NO explicit "Rename selected" action from the user.
//
// Uses its OWN harness instance PER TEST (same pattern as extension-lifecycle.spec.mjs), NOT the
// shared per-worker harness: AutoRenamerOnUpdate is a global extension setting that would leak
// into every other test sharing that worker's instance once enabled, silently changing their
// behavior (e.g. the collision test relies on the default template/no-auto-rename state).
import { test as base, expect } from '../../../e2e/lib/fixtures.mjs';
import { startHarness } from '../../../e2e/lib/harness.mjs';
import { seedVideo } from '../../../e2e/lib/seed-media.mjs';
import { pollUntil } from '../../../e2e/lib/poll.mjs';
import { RENAMER_EXTENSION } from '../lib/renamer-fixtures.mjs';
import { RenamerSettingsPage } from '../lib/pages/renamer-settings-page.mjs';
import { VideoDetailPage } from '../lib/pages/video-detail-page.mjs';

const test = base.extend({
  // eslint-disable-next-line no-empty-pattern
  isolatedHarness: [
    async ({}, use) => {
      const isolatedHarness = await startHarness({ timeoutMs: 180_000 });
      isolatedHarness.owner = await isolatedHarness.bootstrapOwner();
      await isolatedHarness.installExtension(RENAMER_EXTENSION);
      await use(isolatedHarness);
      await isolatedHarness.stop();
    },
    { scope: 'test' },
  ],
});

async function callApi(baseUrl, method, path, body) {
  const res = await fetch(`${baseUrl}${path}`, {
    method,
    headers: body ? { 'Content-Type': 'application/json' } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let json;
  try {
    json = text ? JSON.parse(text) : undefined;
  } catch {
    json = undefined;
  }
  return { status: res.status, ok: res.ok, json, text };
}

test('enabling Auto-rename on update and editing a title through the UI renames the file automatically', async ({
  page,
  isolatedHarness,
}) => {
  const baseUrl = isolatedHarness.baseUrl;
  const api = { get: (p) => callApi(baseUrl, 'GET', p) };

  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();
  await settingsPage.enableAutoRenameOnUpdate();
  await settingsPage.save();

  const video = await seedVideo({ container: isolatedHarness.container, baseUrl });
  const originalPath = video.files[0].path;

  const detailPage = new VideoDetailPage(page, baseUrl);
  await detailPage.goto(video.id);
  await detailPage.openEditTab();
  await detailPage.setTitle('Auto Rename Test Title');

  // No "Rename selected" click anywhere in this test — the hook alone must produce the rename.
  const afterEdit = await pollUntil(
    () => api.get(`/api/videos/${video.id}`).then((r) => r.json),
    (v) => v.files[0].path !== originalPath,
    { label: 'video to be auto-renamed after a title edit', timeoutMs: 20_000 }
  );
  expect(afterEdit.files[0].path).not.toBe(originalPath);
  expect(afterEdit.title).toBe('Auto Rename Test Title');

  // And the grid reflects it too, same as a real user would see without refreshing anything special.
  await page.goto(`${baseUrl}/videos`);
  await page.waitForLoadState('networkidle');
  const filenames = await page.locator('main p').allTextContents();
  expect(filenames).toContain('Auto Rename Test Title');
});

test('with Auto-rename on update left OFF (the default), editing a title does not rename the file', async ({
  page,
  isolatedHarness,
}) => {
  const baseUrl = isolatedHarness.baseUrl;
  const api = { get: (p) => callApi(baseUrl, 'GET', p) };

  // No settings change here — AutoRenamerOnUpdate defaults to false. This is the negative-path
  // counterpart to the test above: confirms the hook is genuinely opt-in, not just untested.
  const video = await seedVideo({ container: isolatedHarness.container, baseUrl });
  const originalPath = video.files[0].path;

  const detailPage = new VideoDetailPage(page, baseUrl);
  await detailPage.goto(video.id);
  await detailPage.openEditTab();
  await detailPage.setTitle('Should Not Trigger Rename');

  // Give the (absent) hook the same window the positive test needs to prove it real — a fixed
  // wait is appropriate here specifically because the assertion is "nothing happened," which
  // pollUntil's early-exit-on-success shape can't express (there's no success condition to poll for).
  await page.waitForTimeout(5_000);

  const afterEdit = await api.get(`/api/videos/${video.id}`).then((r) => r.json);
  expect(afterEdit.files[0].path).toBe(originalPath);
  expect(afterEdit.title).toBe('Should Not Trigger Rename');
});

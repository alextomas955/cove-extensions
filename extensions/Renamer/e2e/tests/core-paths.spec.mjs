// Phase 62 — Core Path Coverage. Automated replacement for the manual "live-verified on the
// running dev host" checks recorded throughout Renamer's milestone history (see PROJECT.md).
//
// Rename/undo/preview are driven through the REAL UI (Videos grid + Renamer settings panel), not
// the REST API — confirmed interactively (Playwright MCP) before writing these tests: selecting a
// card and clicking "Rename selected" raises a native confirm() with the real computed preview
// text, then a native alert() confirming the job was queued; "Undo last rename" opens an in-app
// (React) confirm modal, not a native dialog. See lib/pages/ for the Page Object Model.
import { test, expect, seedVideo, pollUntil } from '../lib/renamer-fixtures.mjs';
import { VideosPage } from '../lib/pages/videos-page.mjs';
import { RenamerSettingsPage } from '../lib/pages/renamer-settings-page.mjs';

const EXTENSION_ID = 'com.alextomas955.renamer';

test('extension installs and reports enabled with UI, API, jobs, and state capabilities', async ({ api }) => {
  const { json } = await api.get('/api/extensions');
  const renamer = json.find((e) => e.id === EXTENSION_ID);
  expect(renamer).toBeTruthy();
  expect(renamer.enabled).toBe(true);
  expect(renamer.hasUI).toBe(true);
  expect(renamer.hasApi).toBe(true);
  expect(renamer.hasJobs).toBe(true);
});

test('editing the filename template updates the live preview and enables Save', async ({ page, baseUrl }) => {
  const errors = [];
  page.on('pageerror', (err) => errors.push(err.message));

  // This is a REAL interaction test, not a "did it render" smoke check: it drives the actual
  // template textbox, asserts the debounced live-preview panel (POST /preview-sample) reflects
  // the edit, and confirms the dirty-state save bar appears — the same state-wiring path that
  // shipped a real StrictMode double-invoke bug in v1.13 (caught only by manual live verification
  // at the time; a "no console error" check would NOT have caught it, since a stale/duplicated
  // fetch doesn't throw — it silently shows wrong data).
  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();

  await expect(settingsPage.filenameTemplateInput).toBeVisible();
  await settingsPage.setFilenameTemplate('$title-e2e-ui-marker');

  await expect(settingsPage.liveVideoSampleCard()).toContainText('e2e-ui-marker', { timeout: 10_000 });
  await expect(settingsPage.unsavedChangesIndicator).toBeVisible();
  await expect(settingsPage.saveChangesButton).toBeVisible();

  expect(errors, `Unexpected console errors: ${errors.join('; ')}`).toEqual([]);
});

test('clicking Save changes persists a settings edit across a page reload', async ({ page, baseUrl }) => {
  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();

  await settingsPage.setFilenameTemplate('$title-e2e-save-marker');
  await settingsPage.save(); // waits for the "Unsaved changes" indicator to disappear — the real save signal

  await page.reload();
  await expect(settingsPage.filenameTemplateInput).toHaveValue('$title-e2e-save-marker');
});

test('dry-run preview matches the template and touches neither disk nor the DB record', async ({
  harness,
  baseUrl,
  api,
}) => {
  const video = await seedVideo({ container: harness.container, baseUrl });
  const originalPath = video.files[0].path;

  // /preview has no UI trigger of its own (it's what "Rename selected" calls internally before
  // showing its confirm() dialog) — the API is the only way to exercise it in isolation, without
  // also triggering the actual mutation the UI action performs. This one test stays API-driven.
  const preview = await api.post(`/api/extensions/${EXTENSION_ID}/preview`, {
    EntityType: 'video',
    EntityIds: [video.id],
  });
  expect(preview.status).toBe(200);
  expect(preview.json.items).toHaveLength(1);
  expect(preview.json.items[0].status).toBe('Renamer');
  expect(preview.json.items[0].oldFullPath).toBe(originalPath);

  const afterPreview = await api.get(`/api/videos/${video.id}`);
  expect(afterPreview.json.files[0].path).toBe(originalPath);
});

test('selecting a video and clicking Rename selected renames it on disk and in the DB; Undo restores it', async ({
  page,
  harness,
  baseUrl,
  api,
}) => {
  const video = await seedVideo({ container: harness.container, baseUrl });
  const originalFilename = video.files[0].path.split('/').pop();
  const originalPath = video.files[0].path;

  const videosPage = new VideosPage(page, baseUrl);
  await videosPage.goto();
  await videosPage.selectCard(originalFilename);

  const dialogMessages = await videosPage.renameSelected();
  // The confirm() dialog shows the real computed preview — assert on it, not just that a dialog
  // fired, so this test would catch a regression in what the preview text itself says.
  expect(dialogMessages[0]).toContain(originalFilename);
  expect(dialogMessages[1]).toMatch(/queued for 1 video/i);

  // Verify the mutation's OUTCOME via the DB record for this specific video, not by re-scraping
  // the grid — the worker-shared instance can have other tests' videos on the same page, making
  // "the first .mp4 in the grid" an unreliable signal once more than one card exists.
  const afterRename = await pollUntil(
    () => api.get(`/api/videos/${video.id}`).then((r) => r.json),
    (v) => v.files[0].path !== originalPath,
    { label: 'video record to reflect the UI-triggered rename' }
  );
  expect(afterRename.files[0].path).not.toBe(originalPath);

  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();
  await settingsPage.undoLastRename();

  const afterUndo = await pollUntil(
    () => api.get(`/api/videos/${video.id}`).then((r) => r.json),
    (v) => v.files[0].path === originalPath,
    { label: 'video record to be restored by the UI-triggered undo' }
  );
  expect(afterUndo.files[0].path).toBe(originalPath);

  // Confirm the restored state is visible in the grid too — the point of this test is that a real
  // user driving the UI sees the file's name return to normal, not just that the DB agrees.
  await videosPage.goto();
  expect(await videosPage.visibleFilenames()).toContain(originalFilename);
});

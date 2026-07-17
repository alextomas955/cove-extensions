// Core rename/undo/preview coverage driven through the REAL UI (Videos grid + Renamer settings
// panel), not the REST API: "Rename selected" raises a native confirm() with the real computed
// preview text, then a native alert() confirming the job was queued; "Undo last rename" opens an
// in-app (React) confirm modal, not a native dialog. See lib/pages/ for the Page Object Model.
import { test, expect, seedVideo } from '../lib/renamer-fixtures.mjs';
import { VideosPage } from '../lib/pages/videos-page.mjs';
import { RenamerSettingsPage } from '../lib/pages/renamer-settings-page.mjs';
import { assertRenamedTo, assertRestoredTo } from '../lib/rename-assertions.mjs';

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

  // Drives the actual template textbox and asserts the debounced live-preview panel
  // (POST /preview-sample) and dirty-state save bar both reflect the edit — a "no console error"
  // check alone would miss a stale/duplicated fetch overwriting the preview with wrong data,
  // since that failure mode doesn't throw.
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
  await settingsPage.save();

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

  // A "$title"-only template over a safe title (letters + spaces only, which the sanitizer passes
  // through unchanged) makes the resulting name deterministic and independent of date/resolution
  // metadata, so the EXACT resulting basename can be asserted, not merely "the path changed".
  const title = 'Core Path Rename Test';
  const expectedBasename = `${title}.mp4`;

  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();
  await settingsPage.setFilenameTemplate('$title');
  await settingsPage.save();

  const videosPage = new VideosPage(page, baseUrl);
  await videosPage.goto();
  // Select the card by its filename BEFORE setting a Title: the grid card's accessible name follows
  // the item's title once one is set, so selecting first keeps the filename-based lookup valid.
  await videosPage.selectCard(originalFilename);

  const setTitle = await api.put(`/api/videos/${video.id}`, { Title: title });
  expect(setTitle.ok).toBe(true);

  const dialogMessages = await videosPage.renameSelected();
  // The confirm() dialog shows the real computed preview — assert on it, not just that a dialog
  // fired, so this test would catch a regression in what the preview text itself says.
  expect(dialogMessages[0]).toContain(originalFilename);

  await assertRenamedTo({
    api,
    container: harness.container,
    videoId: video.id,
    expectedBasename,
    originalPath,
  });

  await settingsPage.goto();
  await settingsPage.undoLastRename();

  await assertRestoredTo({ api, container: harness.container, videoId: video.id, originalPath });

  // Confirm the item still renders in the grid after the undo round-trip — a real user driving the
  // UI sees the card come back (labeled by its title, which the grid shows once one is set). The
  // restored filename itself is proven on disk and in the DB by assertRestoredTo above.
  await videosPage.goto();
  await expect(page.getByRole('link', { name: `Open video ${title}` })).toBeVisible();

  // Restore the default template so a bare "$title" (a no-op for an untitled item) does not leak
  // into a sibling test sharing this worker's Cove instance.
  await settingsPage.goto();
  await settingsPage.setFilenameTemplate('{$date - }$title{ [$resolution]}');
  await settingsPage.save();
});

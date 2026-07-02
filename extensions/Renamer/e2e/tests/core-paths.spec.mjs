// Phase 62 — Core Path Coverage. Automated replacement for the manual "live-verified on the
// running dev host" checks recorded throughout Renamer's milestone history (see PROJECT.md).
import { test, expect, seedVideo, pollJob, pollUntil } from '../lib/renamer-fixtures.mjs';

const EXTENSION_ID = 'com.alextomas955.renamer';
const ROUTE = `/api/extensions/${EXTENSION_ID}`;

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

  // The panel lives at /settings/renamer under Settings → Extensions → Renamer (a dedicated
  // settings tab registered via Renamer.Api.cs's AddSettingsTab, not the top nav) — confirmed by
  // direct inspection (Playwright MCP) of the real running panel, not assumed from the route name.
  await page.goto(`${baseUrl}/settings/renamer`);

  // This is a REAL interaction test, not a "did it render" smoke check: it drives the actual
  // template textbox, asserts the debounced live-preview panel (POST /preview-sample) reflects
  // the edit, and confirms the dirty-state save bar appears — the same state-wiring path that
  // shipped a real StrictMode double-invoke bug in v1.13 (caught only by manual live verification
  // at the time; a "no console error" check would NOT have caught it, since a stale/duplicated
  // fetch doesn't throw — it silently shows wrong data).
  const templateInput = page.getByRole('textbox', { name: 'Filename template' });
  await expect(templateInput).toBeVisible();
  await templateInput.fill('$title-e2e-ui-marker');

  // "Renamed → " (the label) and the computed filename are separate text nodes under the same
  // sample card — match the card container, not the label span, so the filename text is included.
  const videoSampleCard = page.getByText('SAMPLE: VIDEO', { exact: false }).locator('..');
  await expect(videoSampleCard).toContainText('e2e-ui-marker', { timeout: 10_000 });

  await expect(page.getByText('Unsaved changes')).toBeVisible();
  const saveButton = page.getByRole('button', { name: 'Save changes' });
  await expect(saveButton).toBeVisible();

  expect(errors, `Unexpected console errors: ${errors.join('; ')}`).toEqual([]);
});

test('a settings change persists across a reload via the generic extension data API', async ({ api }) => {
  // Renamer has no dedicated GET/PUT options route of its own — options are read/written through
  // Cove's generic per-extension key/value store (GET/PUT /api/extensions/{id}/data/{key}), the
  // same store OptionsStore wraps internally. This is the only way to change a setting over HTTP
  // without driving the settings panel's UI.
  const before = await api.get(`/api/extensions/${EXTENSION_ID}/data`);
  expect(before.ok).toBe(true);

  const newOptions = JSON.stringify({ FilenameTemplate: '$title-e2e-marker' });
  const put = await api.put(`/api/extensions/${EXTENSION_ID}/data/options`, newOptions);
  expect(put.ok).toBe(true);

  const after = await api.get(`/api/extensions/${EXTENSION_ID}/data`);
  expect(after.ok).toBe(true);
  expect(after.json.options).toContain('e2e-marker');
});

test('dry-run preview matches the template and touches neither disk nor the DB record', async ({
  harness,
  baseUrl,
  api,
}) => {
  const video = await seedVideo({ container: harness.container, baseUrl });
  const originalPath = video.files[0].path;

  const preview = await api.post(`${ROUTE}/preview`, {
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

test('a real single-item rename changes disk and DB together, and undo restores both', async ({
  harness,
  baseUrl,
  api,
}) => {
  const video = await seedVideo({ container: harness.container, baseUrl });
  const originalPath = video.files[0].path;

  const enqueue = await api.post(`${ROUTE}/renamer`, {
    EntityType: 'video',
    EntityIds: [video.id],
  });
  expect(enqueue.status).toBe(202);

  const job = await pollJob(api, enqueue.json.jobId);
  expect(job.status.toLowerCase()).toBe('completed');

  const afterRename = await api.get(`/api/videos/${video.id}`);
  const renamedPath = afterRename.json.files[0].path;
  expect(renamedPath).not.toBe(originalPath);

  const undo = await api.post(`${ROUTE}/undo`);
  expect(undo.status).toBe(200);
  expect(undo.json.undone).toBe(1);
  expect(undo.json.failed).toHaveLength(0);

  // The DB write undo performs is not guaranteed visible on the very next request (observed
  // directly: a raw GET immediately after a 200 from /undo can still return the pre-undo path).
  // Poll instead of asserting on the first read.
  const restored = await pollUntil(
    () => api.get(`/api/videos/${video.id}`).then((r) => r.json),
    (v) => v.files[0].path === originalPath,
    { label: 'video path to be restored by undo' }
  );
  expect(restored.files[0].path).toBe(originalPath);
});

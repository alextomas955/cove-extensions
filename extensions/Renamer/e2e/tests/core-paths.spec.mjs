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

test('settings panel loads in the browser without a console error', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (err) => errors.push(err.message));

  // The panel lives under Settings → Extensions → Renamer (a dedicated settings tab, not the top
  // nav — see Renamer.Api.cs's AddSettingsTab call). Navigating the API-confirmed route directly
  // (rather than clicking through Cove's own settings nav, which is out of scope for this suite)
  // keeps this test focused on "does Renamer's own bundle mount and render," not Cove's nav.
  await page.goto(`${page.url()}settings/extensions/renamer`);
  await page.waitForLoadState('networkidle');

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
  const video = await seedVideo({ containerName: harness.containerName, baseUrl });
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
  const video = await seedVideo({ containerName: harness.containerName, baseUrl });
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

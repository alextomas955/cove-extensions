// Phase 63 — Whole-Library Job Flow Coverage. Covers the v1.13 scan-library/renamer-library job
// pair (whole-library Dry Run + Rename All), the first job-polling UI Renamer shipped.
import { test, expect, seedVideo, pollJob, pollUntil } from '../lib/renamer-fixtures.mjs';

const EXTENSION_ID = 'com.alextomas955.renamer';
const ROUTE = `/api/extensions/${EXTENSION_ID}`;

test('scan-library reports every seeded item without mutating any of them', async ({
  harness,
  baseUrl,
  api,
}) => {
  const videos = await Promise.all([
    seedVideo({ container: harness.container, baseUrl, destName: `scan-a-${Date.now()}.mp4` }),
    seedVideo({ container: harness.container, baseUrl, destName: `scan-b-${Date.now()}.mp4` }),
  ]);
  const originalPaths = videos.map((v) => v.files[0].path);
  const seededFileIds = videos.map((v) => v.files[0].id);

  const enqueue = await api.post(`${ROUTE}/scan-library`);
  expect(enqueue.status).toBe(202);

  const job = await pollJob(api, enqueue.json.jobId);
  expect(job.status.toLowerCase()).toBe('completed');

  const result = await api.get(`${ROUTE}/last-scan`);
  expect(result.status).toBe(200);
  const scannedFileIds = result.json.map((item) => item.fileId);
  for (const fileId of seededFileIds) {
    expect(scannedFileIds).toContain(fileId);
  }

  // Scan is read-only — every seeded item's file must be untouched on disk/DB.
  for (let i = 0; i < videos.length; i++) {
    const current = await api.get(`/api/videos/${videos[i].id}`);
    expect(current.json.files[0].path).toBe(originalPaths[i]);
  }
});

test('renamer-library renames every seeded item in one run', async ({ harness, baseUrl, api }) => {
  const videos = await Promise.all([
    seedVideo({ container: harness.container, baseUrl, destName: `lib-a-${Date.now()}.mp4` }),
    seedVideo({ container: harness.container, baseUrl, destName: `lib-b-${Date.now()}.mp4` }),
    seedVideo({ container: harness.container, baseUrl, destName: `lib-c-${Date.now()}.mp4` }),
  ]);
  const originalPaths = videos.map((v) => v.files[0].path);

  const enqueue = await api.post(`${ROUTE}/renamer-library`);
  expect(enqueue.status).toBe(202);

  const job = await pollJob(api, enqueue.json.jobId, { timeoutMs: 60_000 });
  expect(job.status.toLowerCase()).toBe('completed');

  for (let i = 0; i < videos.length; i++) {
    const current = await pollUntil(
      () => api.get(`/api/videos/${videos[i].id}`).then((r) => r.json),
      (v) => v.files[0].path !== originalPaths[i],
      { label: `video ${videos[i].id} to be renamed by renamer-library` }
    );
    expect(current.files[0].path).not.toBe(originalPaths[i]);
  }
});

// Phase 63 — Whole-Library Job Flow Coverage. Covers the v1.13 scan-library/renamer-library job
// pair (whole-library Dry Run + Rename All), the first job-polling UI Renamer shipped.
import { test as base, expect, pollJob, pollUntil } from '../lib/renamer-fixtures.mjs';
import { startHarness } from '@cove-extensions/e2e/harness';
import { seedVideo } from '@cove-extensions/e2e/seed-media';
import { RENAMER_EXTENSION } from '../lib/renamer-fixtures.mjs';

const EXTENSION_ID = 'com.alextomas955.renamer';
const ROUTE = `/api/extensions/${EXTENSION_ID}`;

const test = base.extend({
  // eslint-disable-next-line no-empty-pattern
  isolatedHarness: [
    async ({}, use) => {
      const isolatedHarness = await startHarness();
      isolatedHarness.owner = await isolatedHarness.bootstrapOwner();
      await isolatedHarness.installExtension(RENAMER_EXTENSION);
      await use(isolatedHarness);
      await isolatedHarness.stop();
    },
    { scope: 'test' },
  ],
});

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

// Uses its OWN harness instance PER TEST, unlike scan-library above: renamer-library mutates
// EVERY item in the library, not just the ones this test seeds — under real parallel execution,
// a sibling test in the same worker could have its own seeded/mid-rename video swept into this
// job's "whole library" scope, occasionally missing the polling window for its own rename.
test('renamer-library renames every seeded item in one run', async ({ isolatedHarness }) => {
  const baseUrl = isolatedHarness.baseUrl;
  async function callApi(method, path, body) {
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
  const api = {
    get: (p) => callApi('GET', p),
    post: (p, b) => callApi('POST', p, b),
  };

  const videos = await Promise.all([
    seedVideo({ container: isolatedHarness.container, baseUrl, destName: `lib-a-${Date.now()}.mp4` }),
    seedVideo({ container: isolatedHarness.container, baseUrl, destName: `lib-b-${Date.now()}.mp4` }),
    seedVideo({ container: isolatedHarness.container, baseUrl, destName: `lib-c-${Date.now()}.mp4` }),
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

// The whole-library Dry Run job, driven through the job-polling API.
import { test, expect, ROUTE } from "../lib/renamer-fixtures.mjs";
import { seedVideo } from "@cove-extensions/e2e/seed-media";
import { pollRenamerJob } from "../lib/poll-renamer-job.mjs";

test("scan-library aggregates and pages every seeded item without mutating any of them", async ({
  harness,
  baseUrl,
  api,
}) => {
  // Distinct, searchable basenames: the search case below needs a fragment that matches exactly one
  // of the two, which a shared `scan-` prefix would not give.
  const stamp = Date.now();
  const names = [`scanalpha-${stamp}.mp4`, `scanbravo-${stamp}.mp4`];
  const videos = await Promise.all(
    names.map((destName) => seedVideo({ container: harness.container, baseUrl, destName })),
  );
  const originalPaths = videos.map((v) => v.files[0].path);
  const seededFileIds = videos.map((v) => v.files[0].id);

  const enqueue = await api.post(`${ROUTE}/scan-library`);
  expect(enqueue.status).toBe(202);

  const job = await pollRenamerJob(api, ROUTE, enqueue.json.jobId);
  expect(job.status.toLowerCase()).toBe("completed");

  // The scan persists an aggregate, so the readback reports counts; the rows come from the page query,
  // planned on demand.
  const result = await api.get(`${ROUTE}/last-scan`);
  expect(result.status).toBe(200);
  expect(result.json.totalFiles).toBeGreaterThanOrEqual(seededFileIds.length);
  const statusTotal = result.json.statusCounts.reduce((sum, c) => sum + c.count, 0);
  expect(statusTotal).toBe(result.json.totalFiles);

  const rows = await api.post(`${ROUTE}/scan-rows`, { Take: 500 });
  expect(rows.status).toBe(200);
  const scannedFileIds = rows.json.rows.map((row) => row.fileId);
  for (const fileId of seededFileIds) {
    expect(scannedFileIds).toContain(fileId);
  }

  // The path search runs server-side. A fragment unique to the first name returns that row only.
  const searched = await api.post(`${ROUTE}/scan-rows`, { Take: 500, Query: `scanalpha-${stamp}` });
  expect(searched.status).toBe(200);
  const matchedFileIds = searched.json.rows.map((row) => row.fileId);
  expect(matchedFileIds).toContain(seededFileIds[0]);
  expect(matchedFileIds).not.toContain(seededFileIds[1]);

  // Scan is read-only - every seeded item's file must be untouched on disk/DB.
  for (let i = 0; i < videos.length; i++) {
    const current = await api.get(`/api/videos/${videos[i].id}`);
    expect(current.json.files[0].path).toBe(originalPaths[i]);
  }
});

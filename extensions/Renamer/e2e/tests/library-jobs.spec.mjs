// Whole-library job flow coverage: the scan-library (whole-library Dry Run) and renamer-library
// (Rename All) job pair, driven through the job-polling API.
import { test as base, expect, RENAMER_EXTENSION } from "../lib/renamer-fixtures.mjs";
import { startHarness } from "@cove-extensions/e2e/harness";
import { seedVideo } from "@cove-extensions/e2e/seed-media";
import { assertRenamedTo } from "../lib/rename-assertions.mjs";
import { pollRenamerJob } from "../lib/poll-renamer-job.mjs";

const EXTENSION_ID = "com.alextomas955.renamer";
const ROUTE = `/api/extensions/${EXTENSION_ID}`;

const test = base.extend({
  isolatedHarness: [
    async ({}, use) => {
      const isolatedHarness = await startHarness();
      isolatedHarness.owner = await isolatedHarness.bootstrapOwner();
      await isolatedHarness.installExtension(RENAMER_EXTENSION);
      await use(isolatedHarness);
      await isolatedHarness.stop();
    },
    { scope: "test" },
  ],
});

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

  // The scan persists an AGGREGATE, so the readback reports counts; the rows themselves come from the
  // page query, planned on demand. Asserting both is a stronger check of the same behaviour than the
  // single array read it replaces: the counts must account for the seeded files AND the rows must name them.
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

  // The path search runs server-side now, so only a request over real HTTP proves it works — no unit
  // test can. A fragment unique to the first seeded name must return that row and not its sibling.
  const searched = await api.post(`${ROUTE}/scan-rows`, { Take: 500, Query: `scanalpha-${stamp}` });
  expect(searched.status).toBe(200);
  const matchedFileIds = searched.json.rows.map((row) => row.fileId);
  expect(matchedFileIds).toContain(seededFileIds[0]);
  expect(matchedFileIds).not.toContain(seededFileIds[1]);

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
test("renamer-library renames every seeded item in one run", async ({ isolatedHarness }) => {
  const baseUrl = isolatedHarness.baseUrl;
  const container = isolatedHarness.container;
  async function callApi(method, path, body) {
    const res = await fetch(`${baseUrl}${path}`, {
      method,
      headers: body ? { "Content-Type": "application/json" } : undefined,
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
    get: (p) => callApi("GET", p),
    post: (p, b) => callApi("POST", p, b),
    put: (p, b) => callApi("PUT", p, b),
  };

  // A "$title"-only template over distinct safe titles makes each item's computed name deterministic,
  // so each EXACT resulting basename can be asserted rather than merely "the path changed".
  const setTemplate = await api.put(
    `${ROUTE}/data/options`,
    JSON.stringify({ FilenameTemplate: "$title" }),
  );
  expect(setTemplate.ok).toBe(true);

  const videos = await Promise.all([
    seedVideo({ container, baseUrl, destName: `lib-a-${Date.now()}.mp4` }),
    seedVideo({ container, baseUrl, destName: `lib-b-${Date.now()}.mp4` }),
    seedVideo({ container, baseUrl, destName: `lib-c-${Date.now()}.mp4` }),
  ]);
  const originalPaths = videos.map((v) => v.files[0].path);

  const titles = ["Library Item Alpha", "Library Item Bravo", "Library Item Charlie"];
  for (let i = 0; i < videos.length; i++) {
    const update = await api.put(`/api/videos/${videos[i].id}`, { Title: titles[i] });
    expect(update.ok).toBe(true);
  }

  const enqueue = await api.post(`${ROUTE}/renamer-library`);
  expect(enqueue.status).toBe(202);

  const job = await pollRenamerJob(api, ROUTE, enqueue.json.jobId, { timeoutMs: 60_000 });
  expect(job.status.toLowerCase()).toBe("completed");

  for (let i = 0; i < videos.length; i++) {
    await assertRenamedTo({
      api,
      container,
      videoId: videos[i].id,
      expectedBasename: `${titles[i]}.mp4`,
      originalPath: originalPaths[i],
    });
  }
});

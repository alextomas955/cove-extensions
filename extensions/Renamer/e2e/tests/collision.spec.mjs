// No-clobber collision handling against the released host: its Postgres unique index and the file
// rows its own routes return.
//
// A target-name collision does not skip the second item. It auto-suffixes it through
// DuplicateSuffixFormat, default " ({n})", so both items end up renamed. `SkipCollision` exists as
// a status but is not what a plain duplicate-title collision produces.
//
// It saves a global "$title" filename template, so it takes `restoredOptions`.
// `@smoke` - part of the selection core-paths.spec.mjs explains.
import { test, expect, pollUntil, seedVideo, ROUTE } from "../lib/renamer-fixtures.mjs";
import { assertRenamedTo, basename } from "../lib/rename-assertions.mjs";
import { pollRenamerJob } from "../lib/poll-renamer-job.mjs";

test(
  "renaming two items to the same computed target name auto-suffixes rather than clobbering",
  { tag: "@smoke" },
  async ({ harness, baseUrl, api, restoredOptions: _restoredOptions }) => {
    const container = harness.container;

    // A "$title"-only template makes both items' computed target basename exactly "<title>.mp4",
    // so the auto-suffixed second name can be asserted exactly.
    const setTemplate = await api.put(
      `${ROUTE}/data/options`,
      JSON.stringify({ FilenameTemplate: "$title" }),
    );
    expect(setTemplate.ok).toBe(true);

    const first = await seedVideo({
      container,
      baseUrl,
      destName: `collision-a-${Date.now()}.mp4`,
    });
    const second = await seedVideo({
      container,
      baseUrl,
      destName: `collision-b-${Date.now()}.mp4`,
    });

    // FilenameAsTitle defaults to true, so without an explicit Title each item's $title falls back
    // to its own distinct source basename and no collision occurs.
    const sharedTitle = `Collision Test ${Date.now()}`;
    for (const video of [first, second]) {
      const update = await api.put(`/api/videos/${video.id}`, { Title: sharedTitle });
      expect(update.ok).toBe(true);
    }

    const renameFirst = await api.post(`${ROUTE}/renamer`, {
      EntityType: "video",
      EntityIds: [first.id],
    });
    expect(renameFirst.status).toBe(202);
    const firstJob = await pollRenamerJob(api, ROUTE, renameFirst.json.jobId);
    expect(firstJob.status.toLowerCase()).toBe("completed");

    const firstNewPath = await assertRenamedTo({
      api,
      container,
      videoId: first.id,
      expectedBasename: `${sharedTitle}.mp4`,
      originalPath: first.files[0].path,
    });

    // /preview stays read-only whatever it reports, which the stored path below checks.
    const preview = await api.post(`${ROUTE}/preview`, {
      EntityType: "video",
      EntityIds: [second.id],
    });
    expect(preview.status).toBe(200);
    expect(preview.json.items[0].suffixed).toBe(true);
    expect(preview.json.items[0].newFullPath).not.toBe(firstNewPath);

    const afterPreview = await api.get(`/api/videos/${second.id}`);
    expect(afterPreview.json.files[0].path).toBe(second.files[0].path); // preview touched nothing

    const renameSecond = await api.post(`${ROUTE}/renamer`, {
      EntityType: "video",
      EntityIds: [second.id],
    });
    expect(renameSecond.status).toBe(202);
    const secondJob = await pollRenamerJob(api, ROUTE, renameSecond.json.jobId);
    expect(secondJob.status.toLowerCase()).toBe("completed");

    // A GET immediately after the job reports "completed" can still return the pre-rename path.
    const afterSecond = await pollUntil(
      () => api.get(`/api/videos/${second.id}`).then((r) => r.json),
      (v) => v.files[0].path !== second.files[0].path,
      { label: "second video to be renamed" },
    );
    const secondNewPath = afterSecond.files[0].path;

    expect(basename(secondNewPath)).toBe(`${sharedTitle} (1).mp4`);

    // The second item's own source path must be gone - moved, not copied.
    const secondSourceGone = await container.exec(["test", "-f", second.files[0].path]);
    expect(
      secondSourceGone.exitCode,
      `Second item's source ${second.files[0].path} still exists - not moved`,
    ).not.toBe(0);

    // Both renamed files on disk is what says the second never overwrote the first.
    const firstStillThere = await container.exec(["test", "-f", firstNewPath]);
    expect(
      firstStillThere.exitCode,
      `First item's renamed file ${firstNewPath} is missing - clobbered`,
    ).toBe(0);
    const secondExists = await container.exec(["test", "-f", secondNewPath]);
    expect(secondExists.exitCode, `Second item's renamed file ${secondNewPath} is missing`).toBe(0);
  },
);

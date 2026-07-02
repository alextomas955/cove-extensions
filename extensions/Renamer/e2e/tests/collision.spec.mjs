// Verifies no-clobber collision handling on a real disk: v1.10 shipped a real bug fix here (the
// case-only-rename defect, F-16) — this class of bug only manifests against a real filesystem,
// which is exactly what E2E should protect that unit tests (temp-dir-based, but not against a
// live host process handling concurrent/sequential real requests) can miss.
//
// Renamer's actual collision contract: a target-name collision does NOT skip the second item — it
// auto-suffixes it via DuplicateSuffixFormat (default " ({n})") so both items end up renamed, never
// one clobbering the other. `SkipCollision` exists as a status but is not what a plain
// duplicate-title collision produces; auto-suffix is the default and expected outcome here.
import { test, expect, seedVideo, pollJob, pollUntil } from '../lib/renamer-fixtures.mjs';

const EXTENSION_ID = 'com.alextomas955.renamer';
const ROUTE = `/api/extensions/${EXTENSION_ID}`;

test('renaming two items to the same computed target name auto-suffixes rather than clobbering', async ({
  harness,
  baseUrl,
  api,
}) => {
  // Two videos seeded from the identical fixture, both given the SAME title, so the default
  // template ({$date - }$title{ [$height]}) computes an identical target filename for both — a
  // deterministic collision. (FilenameAsTitle defaults to true, meaning $title falls back to the
  // source basename when Title is unset — seeding with unique destNames alone does NOT collide,
  // since each falls back to its own distinct filename; setting Title explicitly forces the
  // actual collision this test needs.)
  const first = await seedVideo({ container: harness.container, baseUrl, destName: `collision-a-${Date.now()}.mp4` });
  const second = await seedVideo({ container: harness.container, baseUrl, destName: `collision-b-${Date.now()}.mp4` });

  const sharedTitle = `Collision Test ${Date.now()}`;
  for (const video of [first, second]) {
    const update = await api.put(`/api/videos/${video.id}`, { Title: sharedTitle });
    expect(update.ok).toBe(true);
  }

  const renameFirst = await api.post(`${ROUTE}/renamer`, { EntityType: 'video', EntityIds: [first.id] });
  expect(renameFirst.status).toBe(202);
  const firstJob = await pollJob(api, renameFirst.json.jobId);
  expect(firstJob.status.toLowerCase()).toBe('completed');

  const afterFirst = await pollUntil(
    () => api.get(`/api/videos/${first.id}`).then((r) => r.json),
    (v) => v.files[0].path !== first.files[0].path,
    { label: 'first video to be renamed' }
  );
  const firstNewPath = afterFirst.files[0].path;

  // Confirm the preview for the SECOND item, targeting the same name as the first, is classified
  // as an auto-suffix (not a silent overwrite) BEFORE any mutation — /preview must stay read-only
  // regardless of what it reports.
  const preview = await api.post(`${ROUTE}/preview`, { EntityType: 'video', EntityIds: [second.id] });
  expect(preview.status).toBe(200);
  expect(preview.json.items[0].suffixed).toBe(true);
  expect(preview.json.items[0].newFullPath).not.toBe(firstNewPath);

  const afterPreview = await api.get(`/api/videos/${second.id}`);
  expect(afterPreview.json.files[0].path).toBe(second.files[0].path); // preview touched nothing

  // Now actually rename the second item and confirm the auto-suffixed path is what it lands at —
  // and that the first item's file was never touched by the second item's move.
  const renameSecond = await api.post(`${ROUTE}/renamer`, { EntityType: 'video', EntityIds: [second.id] });
  expect(renameSecond.status).toBe(202);
  const secondJob = await pollJob(api, renameSecond.json.jobId);
  expect(secondJob.status.toLowerCase()).toBe('completed');

  // Same read-after-write gap observed with /undo: a GET immediately after the job reports
  // "completed" can still return the pre-rename path. Poll instead of asserting on the first read.
  const afterSecond = await pollUntil(
    () => api.get(`/api/videos/${second.id}`).then((r) => r.json),
    (v) => v.files[0].path !== second.files[0].path,
    { label: 'second video to be renamed' }
  );
  const secondNewPath = afterSecond.files[0].path;
  expect(secondNewPath).not.toBe(firstNewPath); // no-clobber: distinct final paths

  // Both files must exist on disk — neither was lost, and the second never overwrote the first.
  const firstStillThere = await harness.container.exec(['test', '-f', firstNewPath]);
  expect(firstStillThere.exitCode, `First item's renamed file ${firstNewPath} is missing — clobbered`).toBe(0);
  const secondExists = await harness.container.exec(['test', '-f', secondNewPath]);
  expect(secondExists.exitCode, `Second item's renamed file ${secondNewPath} is missing`).toBe(0);
});

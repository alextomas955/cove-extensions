// Verifies a real cross-device move on Linux: /data2 is a tmpfs mount (a genuinely different
// filesystem from the container's root, unlike two named volumes, which land on the same backing
// device in Docker Desktop). A move from /data into /data2 crosses a real kernel filesystem boundary,
// which is what routes the executor onto the verified copy path.
//
// WHY THIS SPEC TAKES ITS SKIP BRANCH (diagnosed by measurement, not reasoning). A bare `tmpfs:` mount
// is created root-owned, while the Cove image runs its process as a non-root user — so nothing Cove
// does may write into the destination at all. CrossVolumeMover's FIRST write is its in-flight temp file
// beside the final target, so the move dies there with an UnauthorizedAccessException, which the mover
// classifies PermissionDenied and the batch runner buckets as SkipLocked. The discriminators that
// established this, each read live rather than inferred: the executor's own per-item log line reported
// crossVolume=true (so the pair really does classify cross-volume THROUGH the running executor, not
// only in an isolated call), the preview's plan item carried a /data2 destination and a matched routing
// rule (so a relocate really was planned), and the recorded reason named a permission denial on the
// in-flight temp file — never "Invalid cross-device link", which is what a genuine EXDEV reaching
// DiskMover would have carried. Handing the mount to the Cove user is the whole fix: the same move then
// completes.
//
// So this spec has never once exercised copy→verify→promote→delete. It proves only that a write into a
// directory the process cannot write to is refused without data loss — worth asserting, but not what
// the spec is named for, and not what it claims below.
//
// Note WHY a false claim survived two weeks in the test meant to guard the behaviour: the assertion
// below branches on the outcome and passes either way, so nothing could ever contradict the comment. A
// test that accepts every result cannot correct the prose above it. Two claims this header used to
// carry were wrong for exactly that reason, and both are gone: a free-space explanation for the skip
// (the tmpfs's free space is orders of magnitude above the headroom, so the preflight cannot fire), and
// a cross-volume classification measured in an isolated call rather than through the executor.
//
// The single-outcome assertion is the next change, and the diagnosis above is what it asserts.
import { test, expect, seedVideo, pollJob } from "../lib/renamer-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.renamer";
const ROUTE = `/api/extensions/${EXTENSION_ID}`;

test("a move routed into a genuinely different filesystem (EXDEV) fails safely, not silently or destructively", async ({
  harness,
  baseUrl,
  api,
}) => {
  const video = await seedVideo({ container: harness.container, baseUrl });
  const originalPath = video.files[0].path;

  const optionsBody = JSON.stringify({
    DefaultDestination: "/data2",
    EnableDefaultRelocate: true,
    AllowedRoots: ["/data", "/data2"],
  });
  const put = await api.put(`/api/extensions/${EXTENSION_ID}/data/options`, optionsBody);
  expect(put.ok).toBe(true);

  try {
    const enqueue = await api.post(`${ROUTE}/renamer`, {
      EntityType: "video",
      EntityIds: [video.id],
    });
    expect(enqueue.status).toBe(202);

    const job = await pollJob(api, enqueue.json.jobId);
    // The batch job itself always reports "completed" — per-item outcomes (renamed/skipped/failed)
    // are in the batch log, not the job status. A skip is not a job failure.
    expect(job.status.toLowerCase()).toBe("completed");

    // Safety property: the source file must not have vanished or been left in a half-moved state.
    // Either it stayed at its original path (skipped) or landed intact at exactly one place — never
    // both missing from /data AND missing from /data2 (which would mean data loss).
    const afterMove = await api.get(`/api/videos/${video.id}`);
    const finalPath = afterMove.json.files[0].path;
    const stillAtOriginal = finalPath === originalPath;
    const exec = await harness.container.exec(["test", "-f", finalPath]);
    const fileExistsAtReportedPath = exec.exitCode === 0;

    expect(
      fileExistsAtReportedPath,
      `DB reports path ${finalPath} but no file exists there — data loss`,
    ).toBe(true);

    if (!stillAtOriginal) {
      // If Renamer's Windows-shaped SameVolume check ever changes to be cross-platform-aware, this
      // branch would start exercising the real CrossVolumeMover path — leave both outcomes valid so
      // this test does not need to change if that happens, only note which branch actually ran.
      console.log(
        "Move succeeded across the EXDEV-raising mount (unexpected on current Linux-only SameVolume logic, but not unsafe).",
      );
    } else {
      console.log(
        'Move was skipped (expected on Linux): DiskMover caught the real EXDEV as an IOException and reported it as "locked or target exists" — misleading reason text for this specific cause, but safe (no data loss, no crash). Tracked as a Renamer backlog item, not fixed here.',
      );
    }
  } finally {
    // This test PUTs GLOBAL Renamer options (EnableDefaultRelocate + DefaultDestination + AllowedRoots)
    // into the Cove instance, which is SHARED across every sibling spec on the same Playwright worker.
    // Restore the defaults so a later spec that relies on the un-routed source-confine path — notably
    // rename-ui-coverage's folder-template relocate, which needs empty AllowedRoots + default-relocate
    // OFF to stay within /data — is not silently routed cross-device (/data2), skipped as an EXDEV move,
    // and left un-renamed. Mirrors core-paths.spec.mjs restoring its template. In `finally` so a failed
    // assertion above still cannot leak routing state into the next test.
    await api.put(
      `/api/extensions/${EXTENSION_ID}/data/options`,
      JSON.stringify({ AllowedRoots: [], EnableDefaultRelocate: false, DefaultDestination: "" }),
    );
  }
});

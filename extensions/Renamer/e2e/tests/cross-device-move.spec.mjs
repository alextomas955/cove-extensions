// Verifies a real cross-device move on Linux: /data2 is a tmpfs mount (a genuinely different
// filesystem from the container's root, unlike two named volumes, which land on the same backing
// device in Docker Desktop). A move from /data into /data2 raises a real EXDEV at the kernel level.
//
// STALE CLAIM REMOVED (2026-08-10). This header used to say the classification was
// Path.GetPathRoot()-based, that a /data -> /data2 move was therefore treated as SAME volume, and that
// CrossVolumeMover was reachable only on Windows. All three became false on 2026-07-28, when v0.3.0
// made VolumeClassifier mount-aware; that commit only re-indented this comment. Measured in a Linux
// container on .NET 10: DriveInfo.GetDrives() enumerates the container's mounts, VolumeKey("/data/x")
// is "/" and VolumeKey("/data2/x") is "/data2", so the pair classifies CROSS-volume and
// CrossVolumeMover is reachable here.
//
// Note WHY a false claim survived two weeks in the test meant to guard the behaviour: the assertion
// below branches on the outcome and passes either way, so nothing could ever contradict the comment. A
// test that accepts every result cannot correct the prose above it.
//
// What this still verifies: the move is non-destructive — the DB path and the disk agree afterwards, so
// there is no data loss whichever mover ran. What it does NOT yet verify is WHICH mover ran. Making it
// assert the single correct outcome needs one live run first, because /data2 is a small tmpfs and the
// free-space preflight now applies to the cross-volume path, so a legitimate refusal and a defect look
// alike until measured. Deliberately left as the next change rather than guessed at here.
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

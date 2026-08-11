// Verifies a real cross-device move on Linux: /data2 is a tmpfs mount (a genuinely different
// filesystem from the container's root, unlike two named volumes, which land on the same backing
// device in Docker Desktop). A move from /data into /data2 crosses a real kernel filesystem
// boundary, which is what routes the executor onto the verified copy path.
//
// WHY THIS SPEC USED TO TAKE ITS SKIP BRANCH (diagnosed by measurement, not reasoning). A bare
// `tmpfs:` mount is created root-owned, while the Cove image runs its process as a non-root user —
// so nothing Cove does may write into the destination at all. CrossVolumeMover's FIRST write is its
// in-flight temp file beside the final target, so the move died there with an
// UnauthorizedAccessException, which the mover classifies PermissionDenied and the batch runner
// buckets as SkipLocked. The discriminators that established this, each read live rather than
// inferred: the executor's own per-item log line reported crossVolume=true (so the pair really does
// classify cross-volume THROUGH the running executor, not only in an isolated call), the preview's
// plan item carried a /data2 destination and a matched routing rule (so a relocate really was
// planned), and the recorded reason named a permission denial on the in-flight temp file — never
// "Invalid cross-device link", which is what a genuine EXDEV reaching DiskMover would have carried.
// Handing the mount to the Cove user is the whole fix: the same move then completes.
//
// So the spec had never once exercised copy→verify→promote→delete. It proved only that a write into
// a directory the process cannot write to is refused without data loss — which is worth asserting,
// and is now asserted deliberately below instead of by accident.
//
// Note WHY a false claim survived two weeks in the test meant to guard the behaviour: the assertion
// used to branch on the outcome and pass either way, so nothing could ever contradict the comment. A
// test that accepts every result cannot correct the prose above it. Two claims this header used to
// carry were wrong for exactly that reason, and neither should come back: a free-space explanation for
// the skip (the tmpfs's free space is orders of magnitude above the headroom, so the preflight cannot
// fire) and a cross-volume classification measured in an isolated call rather than through the
// executor. The replacement for the second is structural rather than editorial — the classification is
// now read from the server's own preview summary on every run, so the spec can no longer disagree with
// the executor about it.
import { test, expect, seedVideo, pollJob, pollUntil } from "../lib/renamer-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.renamer";
const ROUTE = `/api/extensions/${EXTENSION_ID}`;

test("a move across a real filesystem boundary is refused safely while the destination is unwritable, and completes through the verified copy path once it is not", async ({
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
    // The routing half, asserted BEFORE anything runs: without it a classification regression would
    // leave this spec passing while quietly measuring a plain same-volume rename. sameVolumeCount and
    // crossVolumeCount come from the server's own VolumeClassifier over the real paths, so this is the
    // executor's answer rather than one computed here and compared with itself.
    const preview = await api.post(`${ROUTE}/preview`, {
      EntityType: "video",
      EntityIds: [video.id],
    });
    expect(preview.status).toBe(200);
    const planned = preview.json.items.find((i) => i.oldFullPath === originalPath);
    expect(planned, `no plan item for ${originalPath}`).toBeTruthy();
    expect(planned.status).toBe("move");
    expect(planned.newFullPath.startsWith("/data2/")).toBe(true);
    expect(preview.json.summary.crossVolumeCount).toBe(1);
    expect(preview.json.summary.sameVolumeCount).toBe(0);

    // PHASE A — the destination is still root-owned, so the mover cannot write there.
    const batchBefore = await api.get(`${ROUTE}/last-batch`);
    expect(batchBefore.status).toBe(200);

    const refused = await runRenamer(api, video.id);
    expect(refused.status.toLowerCase()).toBe("completed");

    const afterRefusal = await api.get(`/api/videos/${video.id}`);
    expect(
      afterRefusal.json.files[0].path,
      "a refused move must leave the database pointing at the source",
    ).toBe(originalPath);
    await expectFileAt(harness, originalPath, true);
    await expectFileAt(harness, planned.newFullPath, false);

    // Nothing half-written left behind either: the mover removes its in-flight file on any failure,
    // and here it never got to create one.
    const leftovers = await harness.container.exec(["sh", "-c", "ls -A /data2 | wc -l"], {
      user: "root",
    });
    expect(leftovers.output.trim()).toBe("0");

    // A batch header opens before the items run, so a journalled BATCH exists even when every item
    // was refused — the row count is what separates a move that happened from one that did not, and
    // it is also what says whether there is anything to undo.
    const batchAfterRefusal = await api.get(`${ROUTE}/last-batch`);
    expect(batchAfterRefusal.json.count).toBe(0);

    // PHASE B — hand the mount to the Cove user, the way a real deployment's second media volume
    // arrives. This is fixture setup, not a product behaviour under test: nothing the assertions
    // below read is supplied here. Same command and owner install-extension.mjs already uses for
    // /config/extensions. No restore needed — no other spec writes to /data2, and the worker-shared
    // container is a throwaway.
    const handover = await harness.container.exec(["chown", "cove:cove", "/data2"], {
      user: "root",
    });
    expect(handover.exitCode, `handing /data2 to the Cove user failed: ${handover.output}`).toBe(0);

    const completed = await runRenamer(api, video.id);
    expect(completed.status.toLowerCase()).toBe("completed");

    // The one outcome. The file is at the planned destination, the database names that same path, and
    // the source is gone — so it exists in exactly one place, which subsumes both safety properties
    // this spec has always carried (a file exists at whatever path the database reports, and the file
    // is never absent from both locations).
    //
    // Polled, not read once: a settled job does not guarantee the next read reflects its write (the
    // reason poll.mjs exists), and this was measured here — one run reported the source path back from
    // a job whose own log line had already recorded the move. A single read made that a 1-in-N red for
    // a move that had happened. The poll cannot mask a move that did NOT happen: it times out naming
    // the path it kept seeing.
    const finalPath = await pollUntil(
      () => api.get(`/api/videos/${video.id}`).then((r) => r.json.files[0].path),
      (path) => path !== originalPath,
      { label: "the video's stored path to reflect the completed cross-volume move" },
    );
    expect(finalPath).toBe(planned.newFullPath);
    await expectFileAt(harness, finalPath, true);
    await expectFileAt(harness, originalPath, false);

    const batchAfterMove = await api.get(`${ROUTE}/last-batch`);
    expect(batchAfterMove.json.count).toBe(1);
    expect(
      batchAfterMove.json.writtenAtUtcTicks,
      "the batch read back is not the one this move opened",
    ).toBeGreaterThan(batchAfterRefusal.json.writtenAtUtcTicks);
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

/** Enqueues a rename of one video and returns the settled host job. */
async function runRenamer(api, videoId) {
  const enqueue = await api.post(`${ROUTE}/renamer`, {
    EntityType: "video",
    EntityIds: [videoId],
  });
  expect(enqueue.status).toBe(202);
  // The batch job itself always reports "completed" — per-item outcomes (renamed/skipped/failed)
  // are in the batch log, not the job status. A refusal is not a job failure.
  return pollJob(api, enqueue.json.jobId);
}

/** Asserts whether a path exists on the container's disk, naming the path in the failure. */
async function expectFileAt(harness, path, shouldExist) {
  const probe = await harness.container.exec(["test", "-f", path]);
  expect(
    probe.exitCode === 0,
    shouldExist
      ? `expected a file at ${path}, found none`
      : `expected no file at ${path}, found one`,
  ).toBe(shouldExist);
}

// Verifies a real cross-device move on Linux: /data2 is a tmpfs mount (a genuinely different
// filesystem from the container's root, unlike two named volumes which land on the same backing
// device in Docker Desktop — verified empirically before writing this test). A move from /data
// into /data2 raises a real EXDEV at the kernel level.
//
// IMPORTANT — what this test does and does NOT prove: Renamer's own same-vs-cross-VOLUME
// classification (VolumeClassifier.SameVolume) is Path.GetPathRoot()-based, which always returns
// "/" for every path on Linux (POSIX has no drive letters). So Renamer's code still classifies
// this move as "same volume" and routes it through the fast DiskMover.Move path (File.Move), NOT
// through CrossVolumeMover's verified copy->delete path — that path is only reachable on Windows,
// where GetPathRoot returns distinct drive letters. See CrossVolumeMoverTests.cs in the existing
// xUnit suite for coverage of that path.
//
// What IS being verified here: DiskMover.Move's `catch (IOException ex)` already catches the real
// EXDEV .NET raises for a cross-device File.Move on Linux (confirmed: .NET's File.Move surfaces
// EXDEV as a plain IOException, same type as "destination exists" / "source locked") — so the move
// fails SAFELY (reported as a skip, not a crash, not a partial/corrupted state) even though the
// reported reason ("locked or target exists") is misleading for this specific cause. This test
// locks in that safety property and documents the misleading-message gap as a known finding for
// Renamer's own backlog (a message-text fix is a Renamer source change, out of scope for this
// E2E-infrastructure task).
import { test, expect, seedVideo, pollJob } from '../lib/renamer-fixtures.mjs';

const EXTENSION_ID = 'com.alextomas955.renamer';
const ROUTE = `/api/extensions/${EXTENSION_ID}`;

test('a move routed into a genuinely different filesystem (EXDEV) fails safely, not silently or destructively', async ({
  harness,
  baseUrl,
  api,
}) => {
  const video = await seedVideo({ container: harness.container, baseUrl });
  const originalPath = video.files[0].path;

  const optionsBody = JSON.stringify({
    DefaultDestination: '/data2',
    EnableDefaultRelocate: true,
    AllowedRoots: ['/data', '/data2'],
  });
  const put = await api.put(`/api/extensions/${EXTENSION_ID}/data/options`, optionsBody);
  expect(put.ok).toBe(true);

  const enqueue = await api.post(`${ROUTE}/renamer`, {
    EntityType: 'video',
    EntityIds: [video.id],
  });
  expect(enqueue.status).toBe(202);

  const job = await pollJob(api, enqueue.json.jobId);
  // The batch job itself always reports "completed" — per-item outcomes (renamed/skipped/failed)
  // are in the batch log, not the job status. A skip is not a job failure.
  expect(job.status.toLowerCase()).toBe('completed');

  // Safety property: the source file must not have vanished or been left in a half-moved state.
  // Either it stayed at its original path (skipped) or landed intact at exactly one place — never
  // both missing from /data AND missing from /data2 (which would mean data loss).
  const afterMove = await api.get(`/api/videos/${video.id}`);
  const finalPath = afterMove.json.files[0].path;
  const stillAtOriginal = finalPath === originalPath;
  const exec = await harness.container.exec(['test', '-f', finalPath]);
  const fileExistsAtReportedPath = exec.exitCode === 0;

  expect(fileExistsAtReportedPath, `DB reports path ${finalPath} but no file exists there — data loss`).toBe(true);

  if (!stillAtOriginal) {
    // If Renamer's Windows-shaped SameVolume check ever changes to be cross-platform-aware, this
    // branch would start exercising the real CrossVolumeMover path — leave both outcomes valid so
    // this test does not need to change if that happens, only note which branch actually ran.
    console.log('Move succeeded across the EXDEV-raising mount (unexpected on current Linux-only SameVolume logic, but not unsafe).');
  } else {
    console.log('Move was skipped (expected on Linux): DiskMover caught the real EXDEV as an IOException and reported it as "locked or target exists" — misleading reason text for this specific cause, but safe (no data loss, no crash). Tracked as a Renamer backlog item, not fixed here.');
  }
});

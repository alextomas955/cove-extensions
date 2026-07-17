// Shared exact-target rename assertions for the Renamer e2e specs. A rename is "correct" only when
// two independent truth sources agree on the EXACT computed name: the Cove DB record (files[0].path)
// AND the container filesystem. Asserting only one would let a half-applied rename (DB updated but
// no disk move, or a copy that left the source behind) pass.
import { expect } from '@cove-extensions/e2e';
import { pollUntil } from '@cove-extensions/e2e/poll';

/** POSIX basename — container paths are always '/'-separated. */
export function basename(path) {
  return path.slice(path.lastIndexOf('/') + 1);
}

/** POSIX dirname — container paths are always '/'-separated. */
export function dirname(path) {
  const idx = path.lastIndexOf('/');
  return idx <= 0 ? '/' : path.slice(0, idx);
}

/**
 * Asserts a video renamed to EXACTLY `expectedBasename`: the DB record's file basename matches, the
 * file exists on disk at that new path, and `originalPath` is gone from disk. Polls the record so the
 * read-after-write window is honored, then returns the new full path.
 */
export async function assertRenamedTo({ api, container, videoId, expectedBasename, originalPath }) {
  const record = await pollUntil(
    () => api.get(`/api/videos/${videoId}`).then((r) => r.json),
    (v) => basename(v.files[0].path) === expectedBasename,
    { label: `video ${videoId} to be renamed to exactly "${expectedBasename}"` },
  );
  const newPath = record.files[0].path;

  expect(
    basename(newPath),
    `DB record for video ${videoId} should point at exactly "${expectedBasename}", got "${basename(newPath)}"`,
  ).toBe(expectedBasename);

  const newOnDisk = await container.exec(['test', '-f', newPath]);
  expect(newOnDisk.exitCode, `Renamed file "${newPath}" is missing from disk in the Cove container`).toBe(0);

  // The old path being gone is proven on disk, never inferred from the DB path: a DB update with no
  // disk move, or a copy that left the source behind, both leave a stale file while the record reads
  // correct — only a filesystem check catches that leak.
  const oldOnDisk = await container.exec(['test', '-f', originalPath]);
  expect(oldOnDisk.exitCode, `Original path "${originalPath}" still exists on disk after the rename`).not.toBe(0);

  return newPath;
}

/**
 * Asserts undo restored the byte-identical original: the DB record points back at exactly
 * `originalPath` and the file exists on disk there again. Polls so the undo's read-after-write window
 * is honored.
 */
export async function assertRestoredTo({ api, container, videoId, originalPath }) {
  const record = await pollUntil(
    () => api.get(`/api/videos/${videoId}`).then((r) => r.json),
    (v) => v.files[0].path === originalPath,
    { label: `video ${videoId} to be restored to exactly "${originalPath}"` },
  );

  expect(
    record.files[0].path,
    `DB record for video ${videoId} should be restored to exactly "${originalPath}", got "${record.files[0].path}"`,
  ).toBe(originalPath);

  const onDisk = await container.exec(['test', '-f', originalPath]);
  expect(onDisk.exitCode, `Original file "${originalPath}" is not back on disk after undo`).toBe(0);
}

// The undo journal's move out of the extension data store and into a table the extension owns, on
// the path that can lose a user's undo: an installation already carrying a stored journal when the
// new code loads. A fresh install has nothing to migrate, so it passes either way. And because the
// migration deletes its own source, "it ran twice" is not an error anyone would see - it is a
// duplicate batch that silently outranks the real one.
//
// Its own harness per test: it restarts the container and empties the shared journal tables, either
// of which would corrupt a sibling spec running against the same worker instance.
import { isolatedHarnessFixtures } from "@cove-extensions/e2e";
import {
  test as base,
  expect,
  seedVideo,
  pollUntil,
  clientFor,
  queryDb,
  RENAMER_EXTENSION,
  ROUTE,
} from "../lib/renamer-fixtures.mjs";
import { basename } from "../lib/rename-assertions.mjs";
import { pollRenamerJob } from "../lib/poll-renamer-job.mjs";

const test = base.extend(isolatedHarnessFixtures(RENAMER_EXTENSION));

/** .NET UTC ticks for now: 100ns units since 0001-01-01, past 2^53 so it has to be a BigInt. */
function utcNowTicks() {
  return (BigInt(Date.now()) + 62135596800000n) * 10000n;
}

test("a stored journal carried by an upgrading install is migrated into the table, undoes, and never applies twice", async ({
  isolatedHarness,
}) => {
  const harness = isolatedHarness;
  const api = clientFor(harness);

  // A real rename, so the ids, the on-disk move and the recorded old path are the host's own.
  const video = await seedVideo({
    container: harness.container,
    baseUrl: harness.baseUrl,
    token: harness.token,
  });
  const originalPath = video.files[0].path;

  // Without a title the "$title" template renders empty and the rename is a no-op, which would
  // leave nothing journalled to migrate.
  expect((await api.put(`/api/videos/${video.id}`, { Title: "Journal Migration Test" })).ok).toBe(
    true,
  );
  expect(
    (await api.put(`${ROUTE}/data/options`, JSON.stringify({ FilenameTemplate: "$title" }))).ok,
  ).toBe(true);

  const enqueue = await api.post(`${ROUTE}/renamer`, {
    EntityType: "video",
    EntityIds: [video.id],
  });
  expect(enqueue.status).toBe(202);
  expect((await pollRenamerJob(api, ROUTE, enqueue.json.jobId)).status.toLowerCase()).toBe(
    "completed",
  );

  const renamed = await pollUntil(
    () => api.get(`/api/videos/${video.id}`).then((r) => r.json),
    (v) => v.files[0].path !== originalPath,
    { label: `video ${video.id} to have moved off ${originalPath}` },
  );
  const renamedPath = renamed.files[0].path;

  // The row the rename actually journalled, read back so the blob below carries the same entity id,
  // file id and old path the table did.
  const journalled = await queryDb(
    harness,
    "SELECT entity_id || '|' || file_id || '|' || old_path FROM renamer_revert_rows",
  );
  expect(journalled, "the rename must have journalled exactly one row").toMatch(/^\d+\|\d+\|\S+$/);

  // Rewind to the state an upgrading installation is in: the journal lives in the store, and the
  // table is empty because the code that writes it has not run here yet.
  await queryDb(harness, "DELETE FROM renamer_revert_rows; DELETE FROM renamer_revert_batches;");

  const blob = [`#batch|legacy-run|${utcNowTicks()}|Video|open`, journalled].join("\n");
  expect((await api.put(`${ROUTE}/data/revertlog`, blob)).ok).toBe(true);
  expect((await api.put(`${ROUTE}/data/journal-schema`, "2")).ok).toBe(true);

  // A restart is the only way into an initialize-time path: it does not run again while the host is up.
  await harness.restart();

  expect(await queryDb(harness, "SELECT count(*) FROM renamer_revert_batches")).toBe("1");
  expect(await queryDb(harness, "SELECT count(*) FROM renamer_revert_rows")).toBe("1");

  // Both legacy keys gone is what stops an oversized leftover breaking every settings read for the
  // extension, and it is also the migration's own idempotency marker.
  const stored = await api.get(`${ROUTE}/data`);
  expect(stored.ok).toBe(true);
  expect(Object.keys(stored.json ?? {})).not.toContain("revertlog");
  expect(Object.keys(stored.json ?? {})).not.toContain("journal-schema");

  // The migrated batch has to be a real undo rather than rows in a table: the panel is offered it,
  // and running it puts the file back where the stored journal said it came from.
  const summary = await api.get(`${ROUTE}/last-batch`);
  expect(summary.ok).toBe(true);
  expect(summary.json.hasBatch).toBe(true);
  expect(summary.json.count).toBe(1);
  expect(summary.json.consumed).toBe(false);

  const undo = await api.post(`${ROUTE}/undo`, {});
  expect(undo.ok).toBe(true);
  expect(undo.json.undone, `undo reported ${JSON.stringify(undo.json)}`).toBe(1);

  const restored = await pollUntil(
    () => api.get(`/api/videos/${video.id}`).then((r) => r.json),
    (v) => v.files[0].path === originalPath,
    { label: `video ${video.id} to be restored to ${originalPath}` },
  );
  expect(basename(restored.files[0].path)).toBe(basename(originalPath));
  expect((await harness.container.exec(["test", "-f", originalPath])).exitCode).toBe(0);
  expect((await harness.container.exec(["test", "-f", renamedPath])).exitCode).not.toBe(0);

  // A second load must not migrate again. Asserted on the table rather than inferred from the keys
  // being gone, because a duplicate batch outranks the real one silently.
  await harness.restart();
  expect(await queryDb(harness, "SELECT count(*) FROM renamer_revert_batches")).toBe("1");
  expect(await queryDb(harness, "SELECT count(*) FROM renamer_revert_rows")).toBe("0");

  const afterSecondLoad = await api.get(`${ROUTE}/last-batch`);
  expect(afterSecondLoad.json.hasBatch).toBe(true);
  expect(afterSecondLoad.json.consumed, "the migrated batch stays spent across a reload").toBe(
    true,
  );
});

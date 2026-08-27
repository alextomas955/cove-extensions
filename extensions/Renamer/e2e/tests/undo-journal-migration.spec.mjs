// The undo journal's move out of the extension data store and into a table the extension owns,
// proven on the path that can lose a user's undo: an installation that is ALREADY carrying a stored
// journal when the new code loads.
//
// Why this and not the happy path. A fresh install has nothing to migrate, so it passes whether the
// migration works or not; the case that discriminates is an upgrade. And because the migration
// deletes its own source, "it ran twice" is not an error anyone would see — it is a duplicate batch
// that quietly outranks the real one. Both halves are asserted here.
//
// Uses its OWN harness per test. It restarts the container (the only way to reach an initialize-time
// path) and it empties the shared journal tables, either of which would corrupt a sibling spec
// running against the same worker instance.
import { test as base, expect, createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { RENAMER_EXTENSION, seedVideo, pollUntil } from "../lib/renamer-fixtures.mjs";
import { basename } from "../lib/rename-assertions.mjs";
import { pollRenamerJob } from "../lib/poll-renamer-job.mjs";

const EXTENSION_ID = "com.alextomas955.renamer";
const ROUTE = `/api/extensions/${EXTENSION_ID}`;

const test = base.extend({
  isolatedHarness: [
    async ({}, use) => {
      const isolatedHarness = await startHarness();
      try {
        isolatedHarness.owner = await isolatedHarness.bootstrapOwner();
        await isolatedHarness.installExtension(RENAMER_EXTENSION);
        await use(isolatedHarness);
      } finally {
        await isolatedHarness.stop();
      }
    },
    { scope: "test" },
  ],
});

/** .NET UTC ticks for now: 100ns units since 0001-01-01, past 2^53 so it has to be a BigInt. */
function utcNowTicks() {
  return (BigInt(Date.now()) + 62135596800000n) * 10000n;
}

/** Runs one statement in the database container and returns its stdout. */
async function sql(harness, statement) {
  const result = await harness.execDb(
    ["sh", "-c", 'psql -tAX -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "$STATEMENT"'],
    { env: { STATEMENT: statement } },
  );
  expect(result.exitCode, `psql failed for: ${statement}\n${result.output}`).toBe(0);
  return result.output.trim();
}

test("a stored journal carried by an upgrading install is migrated into the table, undoes, and never applies twice", async ({
  isolatedHarness,
}) => {
  const harness = isolatedHarness;
  const api = createApiClient(
    () => harness.baseUrl,
    () => harness.token,
  );

  // A real rename, so the ids, the on-disk move and the recorded old path are the host's own rather
  // than values this test invented and could get subtly wrong.
  const video = await seedVideo({
    container: harness.container,
    baseUrl: harness.baseUrl,
    token: harness.token,
  });
  const originalPath = video.files[0].path;

  // A "$title"-only template over a safe title keeps the rename deterministic and independent of
  // date/resolution metadata. Without a title the template renders empty and the rename is a no-op,
  // which would leave nothing journalled to migrate.
  expect((await api.put(`/api/videos/${video.id}`, { Title: "Journal Migration Test" })).ok).toBe(
    true,
  );
  await api.put(`${ROUTE}/data/options`, JSON.stringify({ FilenameTemplate: "$title" }));

  const enqueue = await api.post(`${ROUTE}/renamer`, {
    EntityType: "video",
    EntityIds: [video.id],
  });
  expect(enqueue.status).toBe(202);
  await pollRenamerJob(api, ROUTE, enqueue.json.jobId);

  const renamed = await pollUntil(
    () => api.get(`/api/videos/${video.id}`).then((r) => r.json),
    (v) => v.files[0].path !== originalPath,
    { label: `video ${video.id} to have moved off ${originalPath}` },
  );
  const renamedPath = renamed.files[0].path;

  // The row the rename actually journalled, read back so the blob below carries the SAME entity id,
  // file id and old path the table did.
  const journalled = await sql(
    harness,
    "SELECT entity_id || '|' || file_id || '|' || old_path FROM renamer_revert_rows",
  );
  expect(journalled, "the rename must have journalled exactly one row").toMatch(/^\d+\|\d+\|\S+$/);

  // Rewind to the state an upgrading installation is in: the journal lives in the store, and the
  // table is empty because the code that writes it has not run here yet.
  await sql(harness, "DELETE FROM renamer_revert_rows; DELETE FROM renamer_revert_batches;");

  const blob = [`#batch|legacy-run|${utcNowTicks()}|Video|open`, journalled].join("\n");
  expect((await api.put(`${ROUTE}/data/revertlog`, blob)).ok).toBe(true);
  expect((await api.put(`${ROUTE}/data/journal-schema`, "2")).ok).toBe(true);

  // A restart is the only way into an initialize-time path: it does not run again while the host is up.
  await harness.restart();

  // (1) The stored journal landed in the table, whole.
  expect(await sql(harness, "SELECT count(*) FROM renamer_revert_batches")).toBe("1");
  expect(await sql(harness, "SELECT count(*) FROM renamer_revert_rows")).toBe("1");

  // (2) Both legacy keys are gone. This is what stops an oversized leftover breaking every settings
  // read for the extension, and it is also the migration's own idempotency marker.
  const stored = await api.get(`${ROUTE}/data`);
  expect(stored.ok).toBe(true);
  expect(Object.keys(stored.json ?? {})).not.toContain("revertlog");
  expect(Object.keys(stored.json ?? {})).not.toContain("journal-schema");

  // (3) The migrated batch is a real undo, not just rows in a table: the panel is offered it, and
  // running it puts the file back where the stored journal said it came from.
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

  // (4) A second load does NOT migrate again. There is nothing left to read, so a re-run would have
  // to invent a batch — and a duplicate batch outranks the real one silently, which is why this is
  // asserted on the table rather than inferred from the keys being gone.
  await harness.restart();
  expect(await sql(harness, "SELECT count(*) FROM renamer_revert_batches")).toBe("1");
  expect(await sql(harness, "SELECT count(*) FROM renamer_revert_rows")).toBe("0");

  const afterSecondLoad = await api.get(`${ROUTE}/last-batch`);
  expect(afterSecondLoad.json.hasBatch).toBe(true);
  expect(afterSecondLoad.json.consumed, "the migrated batch stays spent across a reload").toBe(
    true,
  );
});

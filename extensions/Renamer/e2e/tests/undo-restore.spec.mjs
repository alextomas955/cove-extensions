// The revert journal, proven against a real host on real Postgres — the one tier that can.
//
// Every other tier in this repository proves the SQL string or the port behind it. None of them
// proves that the HOST runs the migration, and that gap is not academic: a failed extension
// migration is a host log line and nothing more (ExtensionManager breaks out of its loop and never
// rethrows), so an extension can load, enable, and answer every request with no table behind it.
// "The extension is enabled" is therefore not evidence. The table is asserted here, in the database
// itself, through the harness's execDb handle.
//
// Two things research could only reason about, recorded as assumptions, are settled here by running
// them instead:
//
//   * Whether the database driver executes a MULTI-STATEMENT migration string in one command the way
//     the command-line client did. That was verified through psql, never through Npgsql. If Npgsql
//     refused it, the failure would be silent in exactly the way described above — so the first
//     assertion below is what turns that assumption into a measurement.
//   * Whether an uninstall/reinstall round trip still loads. Uninstall deletes only the extension's
//     directory, and nothing anywhere deletes a migration receipt, so a reinstall meets a stale table
//     AND a receipt that makes the host skip the migration. The extension must then reuse a table it
//     did not just create. That path is exercised at the end rather than argued from source.
//
// Beyond those: a rename that carries a caption and a neighbour file, an undo that brings all three
// home, and a partial undo that can be retried and acts only on what is left. The panel's own gate
// over that partial batch is read in a browser in the same test body — whether the destructive
// control is offered is decided by whether the batch still has work, and only the pair of states
// distinguishes a gate keyed on that from one keyed on the batch merely existing.
//
// Its own instance per test, not the worker-shared harness: this file renames, undoes, uninstalls and
// reinstalls, all of which are instance-global. On a shared instance the accumulated media also makes
// ordering flaky, which is the recorded reason the isolated fixture exists.
import {
  test as base,
  expect,
  createApiClient,
  isolatedHarnessFixture,
} from "@cove-extensions/e2e";
import { pollUntil, seedVideo, RENAMER_EXTENSION } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";
import { assertRenamedTo, assertRestoredTo } from "../lib/rename-assertions.mjs";
import { pollRenamerJob } from "../lib/poll-renamer-job.mjs";

const EXTENSION_ID = "com.alextomas955.renamer";
const ROUTE = `/api/extensions/${EXTENSION_ID}`;
const MIGRATION_NAME = "001_create_revert_journal";
const MEDIA_DIR = "/data";

const test = base.extend({
  isolatedHarness: isolatedHarnessFixture(RENAMER_EXTENSION),
});

/**
 * Runs one SQL statement in the database container and returns its single unaligned value.
 *
 * The connection details come from the container's own environment rather than being repeated here,
 * so the compose file stays the one place they are written. The statement travels as an environment
 * variable too, which is what lets it hold quotes without any escaping rule to get wrong.
 */
async function queryDb(harness, sql) {
  const result = await harness.execDb(
    ["sh", "-c", 'psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -tAc "$SQL"'],
    { env: { SQL: sql } },
  );
  expect(result.exitCode, `psql failed for [${sql}]: ${result.output}`).toBe(0);
  return result.output.trim();
}

/** How many of the journal's two tables the host actually created. */
function countJournalTables(harness) {
  return queryDb(
    harness,
    "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' " +
      "AND table_name IN ('renamer_revert_batches', 'renamer_revert_rows')",
  );
}

/** Whether the host recorded a receipt for the journal migration (0 or 1). */
function countMigrationReceipts(harness) {
  return queryDb(
    harness,
    "SELECT count(*) FROM extension_migrations " +
      `WHERE extension_id = '${EXTENSION_ID}' AND migration_name = '${MIGRATION_NAME}'`,
  );
}

/** Writes a companion file beside the media, owned by the user Cove runs as. */
async function seedCompanion(container, path, content) {
  await container.copyContentToContainer([{ content, target: path, mode: 0o644 }]);
  await container.exec(["chown", "cove:cove", path], { user: "root" });
}

async function fileExists(container, path) {
  const probe = await container.exec(["test", "-f", path]);
  return probe.exitCode === 0;
}

/** The stem a media path is built on — every companion in this spec shares it. */
function stemOf(path) {
  const name = path.slice(path.lastIndexOf("/") + 1);
  return name.slice(0, name.lastIndexOf("."));
}

test("the host creates the journal on Postgres, undo brings sidecars home, a partial undo retries, and a reinstall still loads", async ({
  page,
  isolatedHarness,
}) => {
  // Several rename/undo rounds against one instance, plus two container restarts (the install the
  // fixture performs and the reinstall at the end). The default per-test budget covers a single
  // rename.
  test.setTimeout(900_000);

  const container = isolatedHarness.container;
  const api = createApiClient(
    () => isolatedHarness.baseUrl,
    () => isolatedHarness.token,
  );
  const stamp = Date.now();

  // ── 1. The host applied the migration ──────────────────────────────────────────────────────────
  //
  // Asserted against the database, not against the extension being enabled — see the header. This is
  // also the only place the driver's handling of the multi-statement migration string is exercised.
  expect(
    await countJournalTables(isolatedHarness),
    "the host did not create both journal tables — an extension migration that fails is only a host log line, so the extension is enabled either way",
  ).toBe("2");
  expect(
    await countMigrationReceipts(isolatedHarness),
    `the host recorded no receipt for ${MIGRATION_NAME}`,
  ).toBe("1");

  // ── 2. A rename carrying two kinds of sidecar, and an undo that brings both back ────────────────
  //
  // Two kinds, because Renamer moves them by two different mechanisms and only one of them has a
  // database row: a caption Cove tracks (.srt, discovered at import) and a configured same-stem
  // neighbour (.nfo, disk only).
  const setOptions = await api.put(
    `${ROUTE}/data/options`,
    JSON.stringify({ FilenameTemplate: "$title", AssociatedExtensions: ["nfo"] }),
  );
  expect(setOptions.ok, `configuring the options failed: ${setOptions.text}`).toBe(true);

  const primaryName = `undo-restore-primary-${stamp}.mp4`;
  const primaryStem = stemOf(primaryName);
  const captionPath = `${MEDIA_DIR}/${primaryStem}.srt`;
  const neighbourPath = `${MEDIA_DIR}/${primaryStem}.nfo`;

  // The caption has to be on disk BEFORE the import: Cove discovers caption sidecars while it
  // processes the video file, so a file written afterwards has no database row and this spec would
  // then be proving the neighbour path twice.
  await seedCompanion(container, captionPath, "1\n00:00:00,000 --> 00:00:01,000\nundo restore\n");
  await seedCompanion(container, neighbourPath, "<nfo><title>undo restore</title></nfo>\n");

  const primary = await seedVideo({
    container,
    baseUrl: isolatedHarness.baseUrl,
    token: isolatedHarness.token,
    destName: primaryName,
  });
  const primaryOriginalPath = primary.files[0].path;

  // Nothing below is meaningful if the fixtures did not arrive. A run that renamed nothing would
  // still exit successfully while inspecting zero input, which is a defect here, not a pass.
  expect(
    await fileExists(container, primaryOriginalPath),
    `seeded media missing: ${primaryOriginalPath}`,
  ).toBe(true);
  expect(await fileExists(container, captionPath), `seeded caption missing: ${captionPath}`).toBe(
    true,
  );
  expect(
    await fileExists(container, neighbourPath),
    `seeded neighbour missing: ${neighbourPath}`,
  ).toBe(true);

  const seeded = await api.get(`/api/videos/${primary.id}`);
  expect(
    seeded.json.files[0].captions.map((c) => c.filename),
    "Cove tracked no caption for the seeded media, so the caption half of undo would be untested",
  ).toEqual([`${primaryStem}.srt`]);

  const primaryTitle = `Undo Restore Primary ${stamp}`;
  const setTitle = await api.put(`/api/videos/${primary.id}`, { Title: primaryTitle });
  expect(setTitle.ok).toBe(true);

  const rename = await api.post(`${ROUTE}/renamer`, {
    EntityType: "video",
    EntityIds: [primary.id],
  });
  expect(rename.status).toBe(202);
  expect((await pollRenamerJob(api, ROUTE, rename.json.jobId)).status.toLowerCase()).toBe(
    "completed",
  );

  await assertRenamedTo({
    api,
    container,
    videoId: primary.id,
    expectedBasename: `${primaryTitle}.mp4`,
    originalPath: primaryOriginalPath,
  });

  const renamedCaptionPath = `${MEDIA_DIR}/${primaryTitle}.srt`;
  const renamedNeighbourPath = `${MEDIA_DIR}/${primaryTitle}.nfo`;
  expect(await fileExists(container, renamedCaptionPath), "the caption did not ride along").toBe(
    true,
  );
  expect(
    await fileExists(container, renamedNeighbourPath),
    "the neighbour did not ride along",
  ).toBe(true);
  expect(
    await fileExists(container, captionPath),
    "the caption's original path still holds a file",
  ).toBe(false);
  expect(
    await fileExists(container, neighbourPath),
    "the neighbour's original path still holds a file",
  ).toBe(false);

  // Polled, never read once: the record behind a completed job is not guaranteed read-your-writes on
  // the very next request, which is the recorded reason these helpers exist.
  const afterRename = await pollUntil(
    () => api.get(`/api/videos/${primary.id}`).then((r) => r.json),
    (v) => v.files[0].captions[0]?.filename === `${primaryTitle}.srt`,
    { label: "the caption row to follow the rename" },
  );
  expect(afterRename.files[0].captions.map((c) => c.filename)).toEqual([`${primaryTitle}.srt`]);

  const undo = await api.post(`${ROUTE}/undo`);
  expect(undo.status, `undo failed: ${undo.text}`).toBe(200);
  expect(undo.json.undone).toBe(1);
  expect(undo.json.failedCount).toBe(0);
  expect(undo.json.skippedCount).toBe(0);
  expect(undo.json.failedSample).toEqual([]);
  expect(undo.json.skippedSample).toEqual([]);

  await assertRestoredTo({
    api,
    container,
    videoId: primary.id,
    originalPath: primaryOriginalPath,
  });

  expect(await fileExists(container, captionPath), "the caption did not come home").toBe(true);
  expect(await fileExists(container, neighbourPath), "the neighbour did not come home").toBe(true);
  expect(
    await fileExists(container, renamedCaptionPath),
    "the caption was left at its renamed path too",
  ).toBe(false);
  expect(
    await fileExists(container, renamedNeighbourPath),
    "the neighbour was left at its renamed path too",
  ).toBe(false);

  const afterUndo = await pollUntil(
    () => api.get(`/api/videos/${primary.id}`).then((r) => r.json),
    (v) => v.files[0].captions[0]?.filename === `${primaryStem}.srt`,
    { label: "the caption row to be restored" },
  );
  expect(
    afterUndo.files[0].captions.map((c) => c.filename),
    "the caption file came back but its database row still names the renamed file",
  ).toEqual([`${primaryStem}.srt`]);

  // ── 3. A partial undo, and a retry that acts only on what is left ───────────────────────────────
  //
  // Two files in ONE batch, then one of the two original slots is occupied so its restore stops for
  // a reason the world can clear. A retryable stop must leave its row in the journal.
  const pairNames = [`undo-restore-a-${stamp}.mp4`, `undo-restore-b-${stamp}.mp4`];
  const pair = [];
  for (const [index, destName] of pairNames.entries()) {
    const video = await seedVideo({
      container,
      baseUrl: isolatedHarness.baseUrl,
      token: isolatedHarness.token,
      destName,
    });
    const title = `Undo Restore Pair ${index} ${stamp}`;
    expect((await api.put(`/api/videos/${video.id}`, { Title: title })).ok).toBe(true);
    pair.push({
      id: video.id,
      fileId: video.files[0].id,
      originalPath: video.files[0].path,
      title,
    });
  }
  for (const item of pair) {
    expect(
      await fileExists(container, item.originalPath),
      `seeded media missing: ${item.originalPath}`,
    ).toBe(true);
  }

  const renamePair = await api.post(`${ROUTE}/renamer`, {
    EntityType: "video",
    EntityIds: pair.map((item) => item.id),
  });
  expect(renamePair.status).toBe(202);
  expect((await pollRenamerJob(api, ROUTE, renamePair.json.jobId)).status.toLowerCase()).toBe(
    "completed",
  );

  for (const item of pair) {
    await assertRenamedTo({
      api,
      container,
      videoId: item.id,
      expectedBasename: `${item.title}.mp4`,
      originalPath: item.originalPath,
    });
  }

  const [blocked, clear] = pair;
  await seedCompanion(container, blocked.originalPath, "an unrelated file the user put back\n");

  const partialUndo = await api.post(`${ROUTE}/undo`);
  expect(partialUndo.status, `undo failed: ${partialUndo.text}`).toBe(200);
  expect(partialUndo.json.undone, "the unobstructed file should have come back").toBe(1);
  // Summed across the two channels rather than read as "failed or skipped", so there is no shape this
  // can pass in two different ways. The COUNTERS are what the response promises to total; the samples
  // beside them are capped, so one file stopping has to show in both.
  expect(
    partialUndo.json.failedCount + partialUndo.json.skippedCount,
    "exactly one file should have stopped",
  ).toBe(1);
  expect(
    [...partialUndo.json.failedSample, ...partialUndo.json.skippedSample].map((e) => e.fileId),
    "the stopped entry should name the obstructed file",
  ).toEqual([blocked.fileId]);

  await assertRestoredTo({ api, container, videoId: clear.id, originalPath: clear.originalPath });

  const afterPartial = await api.get(`${ROUTE}/last-batch`);
  expect(afterPartial.status, `reading the batch summary answered ${afterPartial.status}`).toBe(
    200,
  );
  expect(
    afterPartial.json.count,
    "the batch's original size must not move as rows are restored",
  ).toBe(2);
  expect(afterPartial.json.consumed, "a batch with work left is not spent").toBe(false);

  // ── The same partial batch, read the way the user reads it ──────────────────────────────────────
  //
  // The GATE, on the state the undo above arranged: a batch some of whose files are already back, and
  // one of whose files is still outstanding. The control acts on what is LEFT, so it must still be
  // offered here; the withheld case is asserted after the retry, and only the pair distinguishes a
  // gate keyed on the outstanding work from one keyed on the batch merely existing.
  const settingsPage = new RenamerSettingsPage(page, isolatedHarness.baseUrl);
  await settingsPage.goto();
  await expect(
    settingsPage.undoLastRenameButton,
    "a batch with a file still outstanding is not being offered — the gate has stopped keying on what is left",
  ).toBeVisible();
  await expect(
    settingsPage.undoLastRenameButton,
    "the undo is offered but disabled on a batch that still has work to put back",
  ).toBeEnabled();

  // Clear the obstruction and retry. The second attempt must act on the remainder only.
  await container.exec(["rm", blocked.originalPath], { user: "root" });
  expect(await fileExists(container, blocked.originalPath), "the obstruction is still there").toBe(
    false,
  );

  const retry = await api.post(`${ROUTE}/undo`);
  expect(retry.status, `retry failed: ${retry.text}`).toBe(200);
  expect(retry.json.undone, "the retry must act on the remainder, not on the whole batch").toBe(1);
  expect(retry.json.failedCount).toBe(0);
  expect(retry.json.skippedCount).toBe(0);
  expect(retry.json.failedSample).toEqual([]);
  expect(retry.json.skippedSample).toEqual([]);

  await assertRestoredTo({
    api,
    container,
    videoId: blocked.id,
    originalPath: blocked.originalPath,
  });

  const afterRetry = await pollUntil(
    () => api.get(`${ROUTE}/last-batch`).then((r) => r.json),
    (s) => s.consumed === true,
    { label: "the batch summary to report nothing remaining" },
  );
  expect(afterRetry.count).toBe(2);

  // The gate's other half, now that the batch has nothing left. Read through the panel's own sentence
  // rather than through the control's absence alone; the page object states why an absence check on
  // its own is a green for the wrong reason.
  await settingsPage.goto();
  await settingsPage.waitForNoRenameToUndo();
  await expect(
    settingsPage.undoLastRenameButton,
    "a fully restored batch is still offering its undo — the gate is keying on the batch existing rather than on it having work left",
  ).toHaveCount(0);

  // ── 4. Uninstall, reinstall, and journal a rename on the table that survived ────────────────────
  //
  // The receipt makes the host SKIP the migration on the way back in, so the reinstalled extension
  // has to reuse a table it did not just create. Research could only read this off the host's source.
  const uninstall = await api.post("/api/extensions/registry/uninstall", {
    ExtensionId: EXTENSION_ID,
    UninstallDependents: false,
  });
  expect(uninstall.ok, `uninstall failed: ${uninstall.text}`).toBe(true);
  expect((await api.get("/api/extensions")).json.some((e) => e.id === EXTENSION_ID)).toBe(false);

  expect(
    await countJournalTables(isolatedHarness),
    "uninstall took the journal tables with it — a user's pending undo would be destroyed by an update, which is uninstall-shaped",
  ).toBe("2");
  expect(
    await countMigrationReceipts(isolatedHarness),
    "uninstall removed the migration receipt",
  ).toBe("1");

  await isolatedHarness.installExtension(RENAMER_EXTENSION);
  expect(
    (await api.get("/api/extensions")).json.find((e) => e.id === EXTENSION_ID)?.enabled,
    "the reinstalled extension did not load — a stale table plus an applied receipt is the normal reinstall shape",
  ).toBe(true);

  const revenantName = `undo-restore-reinstall-${stamp}.mp4`;
  const revenant = await seedVideo({
    container,
    baseUrl: isolatedHarness.baseUrl,
    token: isolatedHarness.token,
    destName: revenantName,
  });
  const revenantOriginalPath = revenant.files[0].path;
  expect(
    await fileExists(container, revenantOriginalPath),
    `seeded media missing: ${revenantOriginalPath}`,
  ).toBe(true);

  const revenantTitle = `Undo Restore Reinstall ${stamp}`;
  expect((await api.put(`/api/videos/${revenant.id}`, { Title: revenantTitle })).ok).toBe(true);

  const renameAfterReinstall = await api.post(`${ROUTE}/renamer`, {
    EntityType: "video",
    EntityIds: [revenant.id],
  });
  expect(renameAfterReinstall.status).toBe(202);
  expect(
    (await pollRenamerJob(api, ROUTE, renameAfterReinstall.json.jobId)).status.toLowerCase(),
  ).toBe("completed");

  await assertRenamedTo({
    api,
    container,
    videoId: revenant.id,
    expectedBasename: `${revenantTitle}.mp4`,
    originalPath: revenantOriginalPath,
  });

  // The rename is recorded in the surviving table, not merely reported as done. Exactly one row,
  // because every earlier batch's rows were deleted as their files were restored.
  const journalledRows = await pollUntil(
    () => queryDb(isolatedHarness, "SELECT count(*) FROM renamer_revert_rows"),
    (count) => count === "1",
    { label: "the reinstalled extension to journal its rename into the surviving table" },
  );
  expect(
    journalledRows,
    "the reinstalled extension renamed a file but recorded nothing — a rename it cannot undo",
  ).toBe("1");
});

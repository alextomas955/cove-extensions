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
// Beyond those, the phase's own subject: a rename that carries a caption and a neighbour file, an
// undo that brings all three home, and a partial undo that can be retried and acts only on what is
// left. That partial undo is also read back through the settings panel in a browser, in the same
// test body — the aggregate a real undo leaves and the sentence composed from it were each already
// covered, but by two different tiers in two different runs, and a seam covered by two green runs is
// exactly where a wiring defect survives both. This is the only tier that can hold both halves at
// once, which is why a DOM read appears in a container spec.
//
// A second test in this file covers the one-way migration of an older release's stored journal blob
// into that table. It belongs beside the above for the same reason the table assertion does: the
// migration runs at initialize and nowhere else, so a host is the only thing that can drive it.
//
// Its own instance per test (isolatedTest), not the worker-shared harness: this file renames, undoes,
// uninstalls and reinstalls, all of which are instance-global. On a shared instance the accumulated
// media also makes ordering flaky, which is the recorded reason the isolated fixture exists.
import {
  isolatedTest as test,
  expect,
  pollJob,
  pollUntil,
  seedVideo,
  createApiClient,
  RENAMER_EXTENSION,
} from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";
import { assertRenamedTo, assertRestoredTo } from "../lib/rename-assertions.mjs";

const EXTENSION_ID = "com.alextomas955.renamer";
const ROUTE = `/api/extensions/${EXTENSION_ID}`;
const MIGRATION_NAME = "001_create_revert_journal";
const MEDIA_DIR = "/data";

// The two legacy store keys the blob-to-table migration reads and then deletes, and the stamp it
// requires before it will parse the blob at all. Spelled out here rather than imported because these
// are an on-disk contract an older release wrote: the C# constants may be renamed, but what an
// installed instance carries cannot be, so a test that followed a rename would stop describing the
// data it exists to migrate.
const LEGACY_JOURNAL_KEY = "revertlog";
const LEGACY_SCHEMA_KEY = "journal-schema";
const LEGACY_SCHEMA = "2";

// .NET ticks count 100 ns units from 0001-01-01 and a batch header carries a server-written UTC-ticks
// stamp, so a tick value has to be built from the Unix epoch's own tick offset. BigInt because the
// result is far outside the range a JS number holds exactly.
const UNIX_EPOCH_TICKS = 621355968000000000n;

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

/** How many batches, and how many rows, the journal currently holds. */
async function journalCounts(harness) {
  return {
    batches: await queryDb(harness, "SELECT count(*) FROM renamer_revert_batches"),
    rows: await queryDb(harness, "SELECT count(*) FROM renamer_revert_rows"),
  };
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
  // Four phases against one instance, and two of them restart the container (the install the fixture
  // performs, plus the reinstall at the end). The default per-test budget covers a single rename, not
  // a sequence that boots, renames three times, undoes three times and reinstalls.
  test.setTimeout(900_000);

  const container = isolatedHarness.container;
  const api = createApiClient(() => isolatedHarness.baseUrl, isolatedHarness.token);
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
  expect((await pollJob(api, rename.json.jobId)).status.toLowerCase()).toBe("completed");

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
  // The TOTALS, not the sample lengths: the response describes a bounded sample of each channel, so
  // a run of any size is claimed clean here only if the counts say so.
  expect(undo.json.failedCount).toBe(0);
  expect(undo.json.skippedCount).toBe(0);
  expect(undo.json.warningCount).toBe(0);

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
    const video = await seedVideo({ container, baseUrl: isolatedHarness.baseUrl, destName });
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
  expect((await pollJob(api, renamePair.json.jobId)).status.toLowerCase()).toBe("completed");

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
  // HOW MANY stopped comes from the totals, which is what the response states. Summed across the two
  // channels rather than read as "failed or skipped", so there is no shape this can pass in two
  // different ways.
  expect(
    partialUndo.json.failedCount + partialUndo.json.skippedCount,
    "exactly one file should have stopped",
  ).toBe(1);
  // WHICH one comes from the samples, merged the same way. This is the only thing a sample is read for.
  expect(
    [...partialUndo.json.failedSample, ...partialUndo.json.skippedSample].map((e) => e.fileId),
    "the stopped entry should name the obstructed file",
  ).toEqual([blocked.fileId]);

  await assertRestoredTo({ api, container, videoId: clear.id, originalPath: clear.originalPath });

  // What is left in the journal IS the work left, so the summary the panel reads must say one.
  const afterPartial = await pollUntil(
    () => api.get(`${ROUTE}/last-batch`).then((r) => r.json),
    (s) => s.remainingCount === 1,
    { label: "the batch summary to report one file remaining" },
  );
  expect(afterPartial.count, "the batch's original size must not move as rows are restored").toBe(
    2,
  );
  expect(afterPartial.remainingCount).toBe(1);
  expect(
    afterPartial.unrestorableCount,
    "an occupied slot can be cleared, so nothing is unrestorable",
  ).toBe(0);
  expect(afterPartial.consumed, "a batch with work left is not spent").toBe(false);

  // ── The same partial undo, read the way the user reads it ───────────────────────────────────────
  //
  // Everything above is the aggregate the server computed; this is the sentence the panel composes
  // from it, in the same test body and off the same real undo. The panel refetches /last-batch on
  // mount, so a fresh goto() needs no cache-busting.
  const settingsPage = new RenamerSettingsPage(page, isolatedHarness.baseUrl);
  await settingsPage.goto();
  const renderedStatus = await settingsPage.undoStatusText();
  // The counts clause, TRANSCRIBED BY HAND from what the panel renders — `UndoSection.tsx`'s
  // `Last rename: {status.line}`, and the " · " (U+00B7) separator the pure module it composes that
  // line with joins its parts by — for a batch of two with one of them restored. Deliberately NOT
  // obtained by calling that composer: an expectation computed from the module under test agrees with
  // it however far the two sides drift, which is the one thing this assertion exists to catch.
  // Re-transcribe if either the wording or the separator moves; nothing here derives it.
  expect(
    renderedStatus,
    "the panel's line does not state the counts the undo above actually left behind",
  ).toContain("Last rename: 1 of 2 restored · 1 remaining ·");
  // The retention clause is pinned by its prefix and never by the date it carries: `formatDate` calls
  // toLocaleDateString with an undefined locale, so the rendered date is the BROWSER's, and the en-US
  // that Playwright's Desktop Chrome device yields is a default rather than a contract a differently
  // configured runner has to honour. A verbatim date would flake there and prove nothing more here.
  expect(
    renderedStatus,
    "the panel dropped the retention clause, so the user is never told when the undo stops being offered",
  ).toContain("undo available until");

  // The GATE, on the same arranged state and with no second setup: this batch has exactly one file
  // outstanding of two. That is the case the gate is keyed on — the control acts on what is LEFT, not on
  // what the batch started as — so a spent-but-nonzero batch must still be offered. Nothing read the
  // control's offered state before this, so inverting that gate would have failed nothing.
  await expect(
    settingsPage.undoLastRenameButton,
    "a batch with a file still outstanding is not being offered — the gate has stopped keying on what is left",
  ).toBeVisible();
  await expect(
    settingsPage.undoLastRenameButton,
    "the undo is offered but disabled on a batch that still has work to put back",
  ).toBeEnabled();

  // The destructive confirm's QUOTED COUNT, both strings TRANSCRIBED BY HAND off what the panel renders
  // for an outstanding count of ONE. One rather than two on purpose: this is the singular arm, which a
  // plural-only expectation would pass straight over on a component that never singularizes. Copied
  // verbatim, "their" included — the singular sentence carries a plural possessive, and rewording user
  // facing copy here would invalidate the transcription in the same change that made it.
  //
  // Deliberately NOT interpolated from `afterPartial.remainingCount`, which this spec is already holding
  // a few lines up: an expectation taken from the API response agrees with the server rather than with
  // what the user reads, and the gap between those two is the whole reason these assertions exist. The
  // handle's own name pattern matches any digit, so it can find the control but can never pin the count.
  await settingsPage.openUndoConfirm();
  await expect(
    settingsPage.undoConfirmMessage,
    "the confirm does not quote the one file actually left to put back — a destructive control stating a number the batch no longer holds",
  ).toHaveText("This moves 1 file back to their original names. This can't be undone again.");
  await expect(
    settingsPage.undoConfirmButton,
    "the confirm's action label does not quote the one file actually left to put back",
  ).toHaveText("Undo 1 rename");
  // Dismissed, never accepted: the retry below is what consumes the remainder, and an accept here would
  // undo it early and leave every assertion after this one describing a batch this spec did not arrange.
  await settingsPage.cancelUndoConfirm();

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

  await assertRestoredTo({
    api,
    container,
    videoId: blocked.id,
    originalPath: blocked.originalPath,
  });

  const afterRetry = await pollUntil(
    () => api.get(`${ROUTE}/last-batch`).then((r) => r.json),
    (s) => s.remainingCount === 0,
    { label: "the batch summary to report nothing remaining" },
  );
  expect(afterRetry.count).toBe(2);
  expect(afterRetry.consumed, "a batch with no rows left is spent").toBe(true);

  // The gate's other half, now that the batch is empty. The offered case above passes just as well on a
  // gate keyed on nothing at all, so only the pair distinguishes one keyed on the outstanding count —
  // and this is the half that says a destructive control is not offered when there is nothing to act on.
  // Read through the panel's own sentence rather than through the control's absence alone; the page
  // object states why an absence check on its own is a green for the wrong reason.
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
  const reinstalledApi = createApiClient(() => isolatedHarness.baseUrl, isolatedHarness.token);
  expect(
    (await reinstalledApi.get("/api/extensions")).json.find((e) => e.id === EXTENSION_ID)?.enabled,
    "the reinstalled extension did not load — a stale table plus an applied receipt is the normal reinstall shape",
  ).toBe(true);

  const revenantName = `undo-restore-reinstall-${stamp}.mp4`;
  const revenant = await seedVideo({
    container,
    baseUrl: isolatedHarness.baseUrl,
    destName: revenantName,
  });
  const revenantOriginalPath = revenant.files[0].path;
  expect(
    await fileExists(container, revenantOriginalPath),
    `seeded media missing: ${revenantOriginalPath}`,
  ).toBe(true);

  const revenantTitle = `Undo Restore Reinstall ${stamp}`;
  expect(
    (await reinstalledApi.put(`/api/videos/${revenant.id}`, { Title: revenantTitle })).ok,
  ).toBe(true);

  const renameAfterReinstall = await reinstalledApi.post(`${ROUTE}/renamer`, {
    EntityType: "video",
    EntityIds: [revenant.id],
  });
  expect(renameAfterReinstall.status).toBe(202);
  expect(
    (await pollJob(reinstalledApi, renameAfterReinstall.json.jobId)).status.toLowerCase(),
  ).toBe("completed");

  await assertRenamedTo({
    api: reinstalledApi,
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

// The blob-to-table migration, driven the only way it can be driven end to end: a real containerized
// Cove STARTED ON TOP of a stored legacy journal, with the migrated rows read off the running instance.
//
// The migration is otherwise covered only by tiers that hand a blob to `JournalBlobMigration.RunAsync`
// directly. None of those starts a host, so none can answer whether the host runs the migration at all,
// and "the extension is enabled" is no more evidence here than it is for the table above.
//
// The expectation throughout is the blob this test WROTE. Nothing below is read back from the journal
// to decide what to expect, and the migration is never called to compute it: an expectation obtained
// from the code under test agrees with that code forever.
test("a legacy journal blob the host starts on top of migrates into the table, survives a second start unduplicated, and undoes to the path it named", async ({
  isolatedHarness,
}) => {
  // A container boot plus TWO full restarts plus an undo. The default per-test budget covers a single
  // rename, not a sequence that boots and restarts twice.
  test.setTimeout(900_000);

  const container = isolatedHarness.container;
  const stamp = Date.now();
  const seedApi = createApiClient(() => isolatedHarness.baseUrl, isolatedHarness.token);

  // The extension has already initialized once, with neither legacy key present, so the migration has
  // run and moved nothing. That is what makes the seed below a genuine PRE-migration state rather than
  // a second pass over one already taken — and an empty journal is how that is known rather than
  // assumed, since a row already here would make every count further down unattributable.
  expect(
    (await journalCounts(isolatedHarness)).rows,
    "the journal already holds rows before anything was seeded, so no count below could be attributed to the migration",
  ).toBe("0");

  const video = await seedVideo({
    container,
    baseUrl: isolatedHarness.baseUrl,
    destName: `undo-legacy-${stamp}.mp4`,
  });
  const fileId = video.files[0].id;
  const seededPath = video.files[0].path;
  expect(await fileExists(container, seededPath), `seeded media missing: ${seededPath}`).toBe(true);

  // The path the blob CLAIMS the file was renamed away from, and therefore the path the undo has to put
  // it back at. In the media folder, so the reverse move is an in-folder rename against a folder Cove
  // already holds a row for. The direction is inverted relative to history — a real installation's blob
  // names a path its file genuinely came from — and that inversion is deliberate: it leaves the MIGRATED
  // batch the only batch in the journal, so nothing here can pass on a batch a forward rename opened.
  const claimedOldPath = `${MEDIA_DIR}/undo-legacy-restored-${stamp}.mp4`;
  expect(
    await fileExists(container, claimedOldPath),
    "the restore target already holds a file, so a restore that was skipped to avoid clobbering it would be indistinguishable from one that succeeded",
  ).toBe(false);

  // Two days back: unmistakably not the moment the migration runs, and well inside the retention window
  // so opening the batch does not purge it. The migration stamps a migrated batch with the HEADER's own
  // time on purpose — restamping would silently extend a batch that should already be ageing — and that
  // is a claim its own documentation makes with nothing asserting it.
  const writtenAtTicks = UNIX_EPOCH_TICKS + BigInt(stamp - 2 * 24 * 60 * 60 * 1000) * 10000n;
  const runId = `legacy-run-${stamp}`;

  // The shape is `JournalBlobMigration`'s own reader's, taken from it: a `#batch|runId|ticks|kind|status`
  // header whose status must be the open marker for the batch to be replayable at all, then
  // `entityId|fileId|oldPath` rows. The entity id is the PARENT entity, the file id the physical row.
  const legacyBlob =
    `#batch|${runId}|${writtenAtTicks}|Video|open\n` + `${video.id}|${fileId}|${claimedOldPath}\n`;

  // The raw value, NOT a pre-stringified one. The host's data route binds `[FromBody] string` and so
  // wants exactly one JSON string literal on the wire — and the client already applies that one
  // encoding. Passing a value that has been through JSON.stringify here stores its QUOTED form, whose
  // first character is not the header marker, so the migration reads the whole thing as a headerless
  // blob and silently moves a row of nonsense. That is what the read-back below is for.
  const seededBlob = await seedApi.put(`${ROUTE}/data/${LEGACY_JOURNAL_KEY}`, legacyBlob);
  expect(
    seededBlob.ok,
    `seeding the legacy journal blob answered ${seededBlob.status}: ${seededBlob.text}`,
  ).toBe(true);
  const seededSchema = await seedApi.put(`${ROUTE}/data/${LEGACY_SCHEMA_KEY}`, LEGACY_SCHEMA);
  expect(
    seededSchema.ok,
    `seeding the legacy schema stamp answered ${seededSchema.status}: ${seededSchema.text}`,
  ).toBe(true);

  // Read the store back BEFORE restarting. The migration reads these two keys and nothing else, so a
  // seed the host did not keep — a rejected write, a key spelled differently, a value re-encoded on the
  // way in — would leave every assertion after the restart passing over no input at all.
  const beforeRestart = await seedApi.get(`${ROUTE}/data`);
  expect(
    beforeRestart.ok,
    `reading the extension store back answered ${beforeRestart.status}: ${beforeRestart.text}`,
  ).toBe(true);
  expect(
    beforeRestart.json[LEGACY_JOURNAL_KEY],
    "the host did not keep the legacy journal blob byte-for-byte, so the migration would read something this test did not write",
  ).toBe(legacyBlob);
  expect(
    beforeRestart.json[LEGACY_SCHEMA_KEY],
    "the host did not keep the legacy schema stamp, and without it the migration discards the blob unparsed and reports moving nothing",
  ).toBe(LEGACY_SCHEMA);

  // ── Start the host over on top of it ────────────────────────────────────────────────────────────
  //
  // The migration runs at InitializeAsync and nowhere else, so there is no way to reach it while the
  // host stays up. Everything after this reads the RESTARTED instance: its published host port can be
  // reassigned, and every token minted before the restart is invalid, so the client is rebuilt from the
  // token `restart()` re-minted.
  await isolatedHarness.restart();
  const api = createApiClient(() => isolatedHarness.baseUrl, isolatedHarness.token);

  // A longer budget than the helper's default: this waits for a container restart AND the extension's
  // whole initialize, on a Docker host that may be running a sibling suite. The endpoint answers with
  // no batch until the migration lands, so a short budget would report a defect where the only fault
  // was load.
  const migrated = await pollUntil(
    () => api.get(`${ROUTE}/last-batch`).then((r) => r.json),
    (summary) => summary?.hasBatch === true,
    {
      timeoutMs: 120_000,
      label: "the restarted host to migrate the seeded legacy journal into the journal table",
    },
  );
  expect(
    migrated.count,
    "the migrated batch does not hold exactly the one row the seeded blob described",
  ).toBe(1);
  expect(migrated.remainingCount).toBe(1);
  expect(
    migrated.unrestorableCount,
    "the migrated row arrived already written off, so the undo below would have nothing to do",
  ).toBe(0);
  expect(migrated.consumed, "a migrated batch whose row is still pending is not spent").toBe(false);
  // Compared with a millisecond of slack rather than exactly: the wire carries the stamp as a JSON
  // number, and a tick value is past the range a double holds exactly. A restamp would be seconds out,
  // so the slack cannot hide the failure this checks for.
  expect(
    Math.abs(migrated.writtenAtUtcTicks - Number(writtenAtTicks)),
    "the migrated batch was restamped with the migration's own clock instead of keeping the age the blob gave it",
  ).toBeLessThan(10_000);

  // ── The legacy keys are gone ────────────────────────────────────────────────────────────────────
  //
  // Their deletion IS the migration's idempotency marker — there is deliberately no second flag — so
  // this read is the whole of what stops a re-migration.
  const afterMigration = await api.get(`${ROUTE}/data`);
  expect(afterMigration.ok, `reading the store back answered ${afterMigration.status}`).toBe(true);
  expect(
    Object.keys(afterMigration.json),
    "the legacy journal key survived the migration, so every later start would move its rows again",
  ).not.toContain(LEGACY_JOURNAL_KEY);
  expect(
    Object.keys(afterMigration.json),
    "the legacy schema stamp survived the migration",
  ).not.toContain(LEGACY_SCHEMA_KEY);

  // ── A second start moves nothing ────────────────────────────────────────────────────────────────
  //
  // The cleared keys above are the mechanism; this is the behaviour. Asserted before the undo, because
  // an undo deletes the rows it restores and a duplicate would then have nothing left to show up in.
  await isolatedHarness.restart();
  const afterSecondStart = createApiClient(() => isolatedHarness.baseUrl, isolatedHarness.token);
  await pollUntil(
    () => afterSecondStart.get(`${ROUTE}/last-batch`).then((r) => r.json),
    (summary) => summary?.hasBatch === true,
    { timeoutMs: 120_000, label: "the twice-restarted host to serve the migrated batch again" },
  );
  expect(
    await journalCounts(isolatedHarness),
    "a second start moved the legacy blob again — the migration is not once-only",
  ).toEqual({ batches: "1", rows: "1" });

  // ── The undo puts the file where the blob said it came from ─────────────────────────────────────
  const undo = await afterSecondStart.post(`${ROUTE}/undo`);
  expect(undo.status, `undo over the migrated batch failed: ${undo.text}`).toBe(200);
  expect(undo.json.undone, "the migrated row did not restore").toBe(1);
  expect(undo.json.failedCount).toBe(0);
  expect(undo.json.skippedCount).toBe(0);

  await assertRestoredTo({
    api: afterSecondStart,
    container,
    videoId: video.id,
    originalPath: claimedOldPath,
  });
  expect(
    await fileExists(container, seededPath),
    "the file is at the path the blob named AND still at the one it came from, so the undo copied rather than moved",
  ).toBe(false);
});

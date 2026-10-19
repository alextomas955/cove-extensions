// An instance set to rename what it takes in, and the run refusing rather than acting.
//
// Measured on both builds before this was written. With renaming on, one generation's rename command
// moves every file out of the folder this product named and into a format of its own; the other
// fails the attach outright, because with a new name to write the import is no longer in place, so it
// transfers, the hard link fails, and nothing is recorded. Neither is acceptable, so the run refuses.
//
// WHY THIS DRIVES THE MENU ROW RATHER THAN THE LIBRARY PASS. The setting is read at one gate, and
// that gate is on the path that asks the instance to take a file in. Both generations reach it from
// this row. The library pass reaches it on one of them only, because on the other the pass that
// registers sites asks the instance to take nothing in and therefore reads no setting.
//
// WHAT THE EVIDENCE IS. The instance's own file rows for the seeded entry, and what the route
// answered. Not this product's account of what it did.
//
// WHY THE SPEC WRITES THE SETTING AND THE EXTENSION NEVER DOES. These config routes replace what they
// are sent, so a caller that read one and wrote it back would reset every member it did not carry.
// The extension holds a read and no write; arranging the setting is this spec's own work.
//
// THE SENTENCE IS IMPORTED FROM THE SHIPPED COPY MODULE, because here it is a LOCATOR. One built from
// a hand-copied literal stops finding its notice the day the notice is reworded, silently.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the
// product. A red end-to-end run in this repository is usually the Cove container dying.
import { attemptUntil } from "@cove-extensions/e2e/poll";

import {
  REFLECT_OWNED_SKIPPED_RENAMING_ON,
  WHISPARR_MONITORED,
} from "../../../src/WhisparrSync.Ui/src/common/ui/copy.ts";
import { expect, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import {
  followJob,
  linkIntoPlace,
  monitorStudio,
  openMonitoredMenu,
  pressReflectOwnedAnswering,
  renameOnImport,
  reportedLine,
} from "../../lib/reflect-owned-steps.mjs";

const GESTURE_BUDGET_MS = 60_000;
const JOB_BUDGET_MS = 120_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

/** The instance's own file rows for the seeded entry, polled: its queue runs behind a finished run. */
async function fileRowsAfter(adapter, instance, entryId, label) {
  return await attemptUntil(
    async (_signal, record) => {
      const rows = await adapter.ownedFileRows(instance, entryId);
      record(`${String(rows.length)} file row(s)`);
      return rows.length > 0 ? { value: rows } : null;
    },
    { timeoutMs: JOB_BUDGET_MS, intervalMs: 1_000, label },
  );
}

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    test.use({ generation, ownedMedia: true });

    test("an instance set to rename links nothing and says what to change, and turning it off links the file", async ({
      page,
      baseUrl,
      connected,
    }) => {
      const { adapter, api, instance, owned, studio, studioMonitored } = connected;

      await linkIntoPlace(instance);

      // The bound on both claims below, read off the instance. An entry already carrying a file
      // would make its rows after either run indistinguishable from its rows before them.
      expect(
        await adapter.ownedFileRows(instance, owned.entryId),
        "the seeded entry already holds a file, so nothing found after either run would prove anything",
      ).toEqual([]);

      // ---- Renaming on: the run refuses and names what to change. ----
      await renameOnImport(instance, adapter.renamingMembers, true);

      await monitorStudio(page, baseUrl, studio, studioMonitored);
      const refused = await pressReflectOwnedAnswering(page);
      expect(
        refused?.skipped,
        `with the instance set to rename, the route answered ${JSON.stringify(refused)}; the refusal is what keeps the files it linked from being moved back out`,
      ).toBe("renamingOn");
      expect(
        refused?.jobId ?? null,
        "the refused route still answered a job id, so a run was enqueued for work it had already declined",
      ).toBeNull();

      await expect(
        page.getByRole("status").filter({ hasText: REFLECT_OWNED_SKIPPED_RENAMING_ON }),
        "the refusal left no notice at the control, so a reader who pressed the row is told nothing happened and not what to change",
      ).toBeVisible({ timeout: GESTURE_BUDGET_MS });

      expect(
        await adapter.ownedFileRows(instance, owned.entryId),
        "the instance holds a file for the entry after a run that refused to hand it one",
      ).toEqual([]);
      expect(
        await adapter.ownedEntryHoldsFile(instance, owned.entryId),
        "the entry itself reads as holding a file after a run that handed it none",
      ).toBe(false);

      // ---- Renaming off, and nothing else changed: the same gesture links the file. ----
      await renameOnImport(instance, adapter.renamingMembers, false);

      await openMonitoredMenu(page, baseUrl, studio);
      const enqueued = await pressReflectOwnedAnswering(page);
      expect(
        enqueued?.skipped ?? null,
        `with renaming off the route still refused: ${JSON.stringify(enqueued)}`,
      ).toBeNull();
      expect(
        enqueued?.jobId,
        `the route answered ${JSON.stringify(enqueued)} with no job id, so nothing could follow the run it started`,
      ).toBeTruthy();
      const settled = await followJob(api, enqueued.jobId);

      const { settled: linked, note } = await fileRowsAfter(
        adapter,
        instance,
        owned.entryId,
        "the instance's own file rows for the seeded entry",
      );
      expect(
        linked,
        `the run reported "${String(reportedLine(settled))}" and the instance holds no file for the entry the library named; its file rows last read ${note}`,
      ).toBe(true);
      expect(
        await adapter.ownedEntryHoldsFile(instance, owned.entryId),
        "the instance holds a file row for the entry but the entry itself reads as holding none",
      ).toBe(true);

      // The menu the second gesture was pressed from is still the monitored one, so the two presses
      // were the same row against the same state.
      await expect(page.getByRole("menu", { name: WHISPARR_MONITORED })).toBeVisible();
    });
  });
}

// The folder this product hands an instance, against a running pair whose mounts differ on purpose.
//
// ONE FILESYSTEM AT TWO PATHS. `reflect-owned.shared.spec.mjs` mounts the library's volume in the
// instance at Cove's own path, so a folder handed over unchanged resolves whether or not this
// product re-rooted anything. Here the instance mounts the same volume somewhere else, which is what
// a container deployment really looks like and what hides the defect: a path the instance cannot
// open lists nothing importable, and the run completes reporting no failure.
//
// WHAT THE EVIDENCE IS. The instance's own file rows for the seeded entry, read through the
// generation adapter. Not this product's answer, and not the line the run reports about itself. The
// reported line is asserted separately, and only as the reason a run linked nothing.
//
// THE LIBRARY PATH THAT MAKES THE TWO ROOTS AGREE. Cove reaches the volume at /shared and the
// instance roots its catalogue at its own mount. /shared/media names the same directory as that
// root, so it is added as a Cove library path: the tail is taken below the library root the folder
// sits under, and a root a directory above produces a tail carrying a segment the instance's own
// root already holds.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the
// product. A red end-to-end run in this repository is usually the Cove container dying rather than
// the code under test.
import { attemptUntil, pollUntil } from "@cove-extensions/e2e/poll";
import { addCoveLibraryRoot } from "@cove-extensions/e2e/seed-media";
import { WHISPARR_DATA_MOUNT } from "@cove-extensions/e2e/whisparr";

import { expect, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import {
  followJob,
  linkIntoPlace,
  monitorStudio,
  pressReflectOwned,
  reportedLine,
} from "../../lib/reflect-owned-steps.mjs";

/** The volume as Cove reaches it, and the library path naming the instance's own catalogue root. */
const COVE_SHARED = "/shared";
const COVE_ROOT = `${COVE_SHARED}/media`;

/** What the installation declares before anything here writes to its configuration. */
const COVE_ROOTS = ["/data", "/data2", COVE_SHARED];

const GESTURE_BUDGET_MS = 60_000;
const JOB_BUDGET_MS = 120_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    // The library's own volume, mounted in the instance at a path of the instance's own and the
    // instance's catalogue rooted under it. Cove reaches the same volume at its own path.
    test.use({ generation, ownedMedia: true, instanceMount: WHISPARR_DATA_MOUNT });

    test("an instance mounting the library's volume elsewhere still ends up holding the file", async ({
      page,
      baseUrl,
      connected,
    }) => {
      const { adapter, api, instance, owned, run, studio, studioMonitored } = connected;

      await addCoveLibraryRoot(api, COVE_ROOT, COVE_ROOTS);
      await linkIntoPlace(instance);

      // The bound on the claim below, read off the instance. An entry already carrying a file would
      // make its rows after the run indistinguishable from its rows before it.
      expect(
        await adapter.ownedFileRows(instance, owned.entryId),
        "the seeded entry already holds a file, so a file found after the run would prove nothing",
      ).toEqual([]);

      await monitorStudio(page, baseUrl, studio, studioMonitored);
      const enqueued = await pressReflectOwned(page);
      const settled = await followJob(api, enqueued.jobId);

      const {
        settled: linked,
        value: linkedFiles,
        note,
      } = await attemptUntil(
        async (_signal, record) => {
          const rows = await adapter.ownedFileRows(instance, owned.entryId);
          record(`${String(rows.length)} file row(s)`);
          return rows.length > 0 ? { value: rows } : null;
        },
        {
          timeoutMs: JOB_BUDGET_MS,
          intervalMs: 1_000,
          label: "the instance's own file rows for the seeded entry",
        },
      );
      expect(
        linked,
        `the run reported "${String(reportedLine(settled))}" and the instance holds no file for the entry the library named; its file rows last read ${note}`,
      ).toBe(true);
      const paths = linkedFiles.map((row) => String(row.relativePath ?? row.path));
      expect(
        paths.filter((path) => path.includes(run)).length,
        `the instance holds ${String(paths.length)} file(s) for the entry, which are not the one file the library named: ${paths.join(", ")}`,
      ).toBe(1);

      // The entry's own state and its file rows are separate facts: a file can be registered and
      // attached to nothing, which leaves the entry still reading as one the instance holds none for.
      await pollUntil(
        () => adapter.ownedEntryHoldsFile(instance, owned.entryId),
        (holds) => holds === true,
        {
          timeoutMs: GESTURE_BUDGET_MS,
          intervalMs: 2_000,
          label: "the instance's own catalogue entry reads as holding a file",
        },
      );
    });

    // WHAT THIS ESTABLISHES, and what it does not. With no Cove library path naming the instance's
    // own catalogue root, every path the product forms is one the instance cannot see: its mount is
    // its own and it holds no /shared at all. So this establishes that a folder the instance cannot
    // see is reported as that, and it establishes nothing about a mapping - no case here supplies
    // one. The sentence is transcribed rather than imported because the run composes it in the
    // backend, which a spec has no module to import.
    test("an instance that cannot reach the library's volume leaves a run naming the path it could not see", async ({
      page,
      baseUrl,
      connected,
    }) => {
      const { adapter, api, instance, owned, run, studio, studioMonitored } = connected;

      await linkIntoPlace(instance);

      await monitorStudio(page, baseUrl, studio, studioMonitored);
      const enqueued = await pressReflectOwned(page);
      const settled = await followJob(api, enqueued.jobId);

      const line = reportedLine(settled);
      expect(
        line,
        `the run completed and reported no line at all. Its line is the only place the run is reported: the gesture that started it was answered before it read anything. The whole status was ${JSON.stringify(settled)}`,
      ).toBeTruthy();
      expect(
        line,
        `the run reported "${String(line)}", which does not say that it could address no folder. A count of zero reads as a clean pass over every folder the entity holds`,
      ).toMatch(/could be linked: /);
      // The seeded file carries this execution's own id, so a line naming it is a line naming the
      // path the instance was really asked about rather than any path at all.
      expect(
        line,
        `the run reported "${String(line)}" without naming a path it tried, so a reader is told nothing they can act on`,
      ).toContain(run);

      expect(
        await adapter.ownedFileRows(instance, owned.entryId),
        `the run reported "${String(line)}" and the instance holds a file for the entry anyway, so the folder reached it after all. The run is ${run}`,
      ).toEqual([]);
    });
  });
}

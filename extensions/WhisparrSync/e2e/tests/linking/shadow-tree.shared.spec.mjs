// Building a folder this extension owns for one entity, filling it with links to that entity's
// library files, and keeping the host's own scan out of it.
//
// One scenario, collected once per generation, each execution against an installation of its own.
// The generation is a fixture option rather than anything this body reads: both generations keep a
// tree of their own, and what each calls its tree folder comes from the adapter.
//
// WHY THE EVIDENCE IS A SHELL. Cove cannot see the tree, and that is the capability rather than an
// obstacle: the ignore file at the tree root is what keeps the host's scan out of everything below
// it. So there is no Cove route that answers what is in there. The instance's own filesystem route
// reads a directory but reports no identity, and identity is what tells a second NAME for a file
// from a second COPY of it. Both are read with `stat` in a container mounting the volume.
//
// WHAT THIS DOES NOT ESTABLISH. That the registration the run sends names the folder. Neither
// generation creates a catalogue entry for an identifier it cannot resolve against its vendor's
// metadata service, and no stand-in for that service is wired here, so the run's add lands on an
// entry the fixture seeded rather than creating one. The folder reaching the add body is asserted
// in the backend suite, against the body the instance received.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the
// product. A red end-to-end run in this repository is usually the Cove container dying.
import { attemptUntil, pollUntil } from "@cove-extensions/e2e/poll";

import { expect, extensionRoute, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import {
  distinctMediaUnder,
  mediaUnder,
  identityOf,
  ignoreFileIn,
  namesIn,
  sharedBetweenBothProducts,
} from "../../lib/tree-steps.mjs";

/**
 * The volume both containers reach, which the installation already declares as a library root.
 *
 * Taken as it stands rather than narrowed to the instance's own catalogue root under it. An entity's
 * tree goes under the root most of its files sit under, and a second root nested inside the first
 * makes that a tie the configured order settles rather than the layout.
 */
const COVE_ROOT = "/shared";

const RUN_BUDGET_MS = 240_000;
const SCAN_BUDGET_MS = 240_000;

/** The one line the ignore file carries, which is what makes the host skip everything below it. */
const IGNORE_EVERYTHING = "*";

test.describe.configure({ timeout: SPEC_BUDGET_MS });

/**
 * The two figures the run's own ending states, or null where it states neither.
 *
 * Transcribed from the line the product composes rather than parsed loosely: a pattern that matched
 * any number would pass on a line that named some other figure.
 */
function figuresIn(summary) {
  const read = /(\d[\d,]*) given a folder of their own, (\d[\d,]*) linked/.exec(summary);
  if (read === null) return null;

  return {
    givenAFolder: Number(read[1].replaceAll(",", "")),
    linked: Number(read[2].replaceAll(",", "")),
  };
}

/** Follows one enqueued host job to a settled state, whichever list it is on by then. */
async function followHostJob(api, jobId, label) {
  const { settled, value, note } = await attemptUntil(
    async (_signal, record) => {
      const listed = await api.get("/api/jobs");
      const history = await api.get("/api/jobs/history");
      const found = [...(listed.json ?? []), ...(history.json ?? [])].find(
        (job) => job.id === jobId,
      );
      record(`state ${String(found?.status ?? "absent")}`);
      return /complete|fail|cancel/i.test(String(found?.status)) ? { value: found } : null;
    },
    { timeoutMs: SCAN_BUDGET_MS, intervalMs: 2_000, label },
  );
  expect(settled, `${label} never settled; the host last reported ${note}`).toBe(true);
  return value;
}

/** How many videos the library holds, read off the host's own count rather than a page length. */
async function videoCount(api) {
  const answered = await api.get("/api/videos?page=1&pageSize=1");
  expect(
    answered.status,
    `the library count could not be read: ${String(answered.text).slice(0, 300)}`,
  ).toBe(200);
  return Number(answered.json?.totalCount ?? answered.json?.total ?? NaN);
}

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    // One filesystem at one path in both containers, with the instance's catalogue rooted on it.
    // A tree lives on the drive its files live on, so the library's own volume is what this needs.
    test.use({ generation, ownedMedia: true });

    test("the entity gets a folder of its own holding a second name for its file, and the host's scan never sees it", async ({
      api,
      connected,
      isolatedCove,
    }) => {
      const { adapter, owned } = connected;
      const cove = isolatedCove.container;

      await sharedBetweenBothProducts(cove, COVE_ROOT);

      const treeRoot = `${COVE_ROOT}/${adapter.treeFolder}`;
      const entityFolder = `${treeRoot}/${owned.registeredAs}`;

      // The bounds the claims below are measured against, read before the run rather than assumed.
      const libraryFiles = await mediaUnder(cove, COVE_ROOT);
      const filesBefore = await distinctMediaUnder(cove, COVE_ROOT);
      const videosBefore = await videoCount(api);
      expect(
        libraryFiles,
        "the library holds no file under the instance's own root, so the run below has nothing to link",
      ).not.toEqual([]);
      expect(
        await namesIn(cove, treeRoot),
        "a tree is already there before the run, so anything found after it would prove nothing",
      ).toEqual([]);

      const started = await api.post(extensionRoute("sync/run"), { alsoMonitor: false });
      expect(
        started.json?.refusal ?? "none",
        `the run refused before it started: ${String(started.text).slice(0, 300)}`,
      ).toBe("none");
      const run = await pollUntil(
        async () =>
          (await api.get(extensionRoute(`job-status/${String(started.json?.jobId)}`))).json,
        (one) => /complete|fail/i.test(String(one?.status)),
        { timeoutMs: RUN_BUDGET_MS, intervalMs: 2_000, label: "the library run's own job status" },
      );
      expect(run?.error ?? null, `the run faulted: ${String(run?.error)}`).toBeNull();

      // The folder, and one name in it per file the entity owns. The message names which of the
      // three steps did not happen rather than only that the folder is empty: the tree root absent
      // is a build that never ran, the entity folder absent is a build that could not make it, and
      // an empty folder is a build whose links were refused.
      const linkNames = await namesIn(cove, entityFolder);
      const diagnostics = JSON.stringify({
        summary: run?.summary,
        treeRoot: await namesIn(cove, treeRoot),
        libraryRoot: await namesIn(cove, COVE_ROOT),
        libraryFiles,
        entityFolderOnDisk: await identityOf(cove, entityFolder),
        whereTheInstanceHoldsIt: await adapter.entryPath(connected.instance, owned.entryId),
      });
      expect(linkNames.length, `nothing was linked into ${entityFolder}: ${diagnostics}`).toBe(
        libraryFiles.length,
      );

      // A second NAME, not a second copy. Both halves matter: one identity says the bytes are not
      // duplicated, and a name count of two says the library's own name is still there.
      const linked = await identityOf(cove, `${entityFolder}/${linkNames[0]}`);
      const library = await identityOf(cove, libraryFiles[0]);
      expect(linked, `nothing is at ${entityFolder}/${String(linkNames[0])}`).not.toBeNull();
      expect(
        { device: linked?.device, number: linked?.number },
        `the name in the entity's folder is a different file from ${libraryFiles[0]}, so the bytes were copied`,
      ).toEqual({ device: library?.device, number: library?.number });
      expect(
        linked?.names,
        "the file the link points at carries one name, so the library's own name is gone",
      ).toBe(2);
      // Names rise by one per link and identities do not. Both halves are read: the identity count
      // alone would also hold for a run that linked nothing.
      expect(
        await mediaUnder(cove, COVE_ROOT),
        "the library root holds no more names than before, so nothing was linked into the tree",
      ).toHaveLength(libraryFiles.length + linkNames.length);
      expect(
        await distinctMediaUnder(cove, COVE_ROOT),
        "the number of files under the library root changed, so something was copied rather than linked",
      ).toBe(filesBefore);

      // The file that keeps the host's scan out, at the top of the tree rather than in the folder
      // below it: written there it would leave every other entity's folder discoverable.
      expect(
        await ignoreFileIn(cove, treeRoot),
        `the tree root ${treeRoot} carries no ignore file the host's scan will honour`,
      ).toBe(IGNORE_EVERYTHING);
      expect(await ignoreFileIn(cove, entityFolder)).toBeNull();

      // The host's own scan, run over a library that now holds a tree.
      const scan = await api.post("/api/jobs/scan");
      expect(scan.status, `the scan was refused: ${String(scan.text).slice(0, 300)}`).toBeLessThan(
        400,
      );
      await followHostJob(api, scan.json?.jobId, "the host's own library scan");

      expect(
        await videoCount(api),
        "the scan discovered something after the tree was built, so the links reached the library",
      ).toBe(videosBefore);

      // The ending a reader is left with. The figures are read out of the line rather than
      // recomputed: a spec that worked them out the way the product does would agree with the
      // product however wrong both were. They are compared against what is on disk.
      //
      // Which path the instance holds the entity at is not compared here, for the reason at the
      // top of this file: the entry the run reaches was seeded rather than created, so it stays
      // where the fixture put it.
      const reported = figuresIn(String(run?.summary));
      expect(
        reported,
        `the run's ending states neither figure: ${String(run?.summary)}`,
      ).not.toBeNull();
      expect(
        reported?.givenAFolder,
        "the run says a different number of entities got a folder from the number of folders in the tree",
      ).toBe((await namesIn(cove, treeRoot)).filter((name) => name !== ".coveignore").length);
      expect(
        reported?.linked,
        "the run says a different number of files were linked from the number of names in the folder",
      ).toBe(linkNames.length);
    });
  });
}

// A library arranged the way readers arrange libraries, and every entity in it registering anyway.
//
// WHAT IS BEING CLAIMED. What a reader can link stops depending on how their own folders are
// arranged. Both generations refuse a second entity at a folder another entity already uses, so a
// library that keeps several entities' files in one folder can register one of them and no more.
// The folder this extension builds for each entity is what removes that: every entity gets a folder
// of its own, holding a second name for each file it owns.
//
// WHICH ARRANGEMENTS ARE DRIVEN, AND WHY NOT THE OTHERS. Four were measured before this was
// written: a folder per studio, a folder per scene, everything in one folder, and folders by year.
// The first two already register on the generation each of them suits, because one folder then
// holds one entity's files and that is the shape the instance models. The two driven here are the
// ones that cannot be expressed without a tree: everything in one folder, where the instance
// registers one of the entities sharing it, and folders by year, which groups by something no
// catalogue models at all. The absence of the other two is not a gap.
//
// The names are the third axis. A library whose file names carry nothing an instance can parse is
// driven as its own layout, because what the run hands the instance is a folder of links named for
// the identities of the files they point at, which no instance parses anything out of either, and
// the entry each one belongs to is supplied outright rather than read off the name.
//
// THE LAYOUT DECIDES THE SEEDING AND REACHES NO ASSERTION. Every assertion below is shared: which
// folder the files were in and what they were called is the fixture's business, and the body reads
// only what the instance now holds and what is on disk.
//
// WHAT THIS DOES NOT ESTABLISH. What the instance RECORDED against each entry. A file is recorded
// from a folder of links only where the instance declares a root above that folder, and on one
// generation only where the scene's own number can be resolved against the metadata service that
// numbers it - a hosted service no sealed run reaches. The recording is driven on a stack with
// real credentials instead. What is driven here is what the tree exists for: the registration and
// the links.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the
// product. A red end-to-end run in this repository is usually the Cove container dying.
import { pollUntil } from "@cove-extensions/e2e/poll";

import { expect, extensionRoute, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import {
  distinctMediaUnder,
  identityOf,
  LIBRARY_LAYOUTS,
  mediaUnder,
  namesIn,
  sharedBetweenBothProducts,
} from "../../lib/tree-steps.mjs";

/** The volume both containers reach, which the installation already declares as a library root. */
const COVE_ROOT = "/shared";

const RUN_BUDGET_MS = 240_000;

/** What each arrangement is called in a test's own name, so a failure says which one it was. */
const ARRANGED = {
  flat: "every file in one folder",
  byYear: "its folders by year",
  unnamed: "file names nothing can parse",
};

test.describe.configure({ timeout: SPEC_BUDGET_MS });

/** Runs the library over the whole library and follows it to a settled state. */
async function runOverTheLibrary(api) {
  const started = await api.post(extensionRoute("sync/run"), { alsoMonitor: false });
  expect(
    started.json?.refusal ?? "none",
    `the run refused before it started: ${String(started.text).slice(0, 300)}`,
  ).toBe("none");

  const run = await pollUntil(
    async () => (await api.get(extensionRoute(`job-status/${String(started.json?.jobId)}`))).json,
    (one) => /completed|failed|cancelled/i.test(String(one?.status)),
    { timeoutMs: RUN_BUDGET_MS, intervalMs: 2_000, label: "the library run's own job status" },
  );
  expect(run?.error ?? null, `the run faulted: ${String(run?.error)}`).toBeNull();
  expect(
    String(run?.status).toLowerCase(),
    `the run did not complete: ${JSON.stringify(run)}`,
  ).toBe("completed");
  return run;
}

for (const [arrangement, layout] of Object.entries(LIBRARY_LAYOUTS)) {
  for (const generation of ["v3", "v2"]) {
    test.describe(`Whisparr ${generation}, a library with ${ARRANGED[arrangement]}`, () => {
      test.use({ generation, libraryLayout: layout });

      test("every entity the library holds files for is registered at a folder of its own, holding a second name for each of those files", async ({
        api,
        connected,
        isolatedCove,
      }) => {
        const { adapter, instance, layout: seeded } = connected;
        const cove = isolatedCove.container;

        await sharedBetweenBothProducts(cove, COVE_ROOT);

        // The premise, read rather than assumed. Every entity's files in ONE folder is what the
        // instance refuses to register, and a fixture that put each entity's files somewhere of
        // their own would leave every assertion below passing for a reason that is not the product.
        const shared = await namesIn(cove, seeded.folder);
        for (const entity of seeded.entities) {
          for (const file of entity.files) {
            expect(
              shared,
              `the premise did not hold: ${file.name} is not in ${seeded.folder}, so this library does not keep several entities' files in one folder`,
            ).toContain(file.name);
          }
        }

        const libraryFiles = await mediaUnder(cove, COVE_ROOT);
        const filesBefore = await distinctMediaUnder(cove, COVE_ROOT);
        const treeRoot = `${COVE_ROOT}/${adapter.treeFolder}`;
        expect(
          await namesIn(cove, treeRoot),
          "a tree is already there before the run, so anything found after it would prove nothing",
        ).toEqual([]);

        const run = await runOverTheLibrary(api);

        // One folder per entity, each holding one name per file that entity owns, and the instance
        // itself recording the entity there. The message carries the run's own ending, because a
        // run that registered nothing and a run that could not build a folder read alike here.
        let linkedNames = 0;
        for (const entity of seeded.entities) {
          const entityFolder = `${treeRoot}/${entity.registeredAs}`;
          const linked = await namesIn(cove, entityFolder);
          expect(
            linked.length,
            `${entityFolder} holds a name for ${String(linked.length)} of the ${String(entity.files.length)} files its entity owns; the run reported "${String(run?.summary)}"`,
          ).toBe(entity.files.length);
          linkedNames += linked.length;

          for (const name of linked) {
            const read = await identityOf(cove, `${entityFolder}/${name}`);
            expect(
              read?.names,
              `${entityFolder}/${name} is the only name its file has, so it is a copy rather than a second name`,
            ).toBeGreaterThanOrEqual(2);
          }

          expect(
            await adapter.entryPath(instance, entity.entryId),
            `the instance does not hold this entity at the folder built for it; the run reported "${String(run?.summary)}"`,
          ).toBe(entityFolder);
        }

        // Nothing was copied. Both halves are read: the identity count alone would also hold for a
        // run that linked nothing at all.
        expect(
          await mediaUnder(cove, COVE_ROOT),
          "the library root holds no more names than before, so nothing was linked into the tree",
        ).toHaveLength(libraryFiles.length + linkedNames);
        expect(
          await distinctMediaUnder(cove, COVE_ROOT),
          "the number of files under the library root changed, so something was copied rather than linked",
        ).toBe(filesBefore);
      });
    });
  }
}

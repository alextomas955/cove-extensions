// A library file that changes drive, and the name left behind on the drive it was on.
//
// A hard link cannot cross a filesystem, so a file that moves to another drive leaves its link
// holding the old bytes: the library's copy is on the new drive and the old one is reachable through
// that name alone. This drives that, and ends with the name taken back and the file's bytes existing
// once.
//
// WHY /data2. Two Docker named volumes land on one host filesystem, so a move between them is an
// ordinary rename and would prove nothing. The installation's /data2 is a tmpfs mount, which is a
// different device at the kernel level, and the assertion below reads both devices rather than
// assuming it.
//
// WHAT THIS DOES NOT ESTABLISH, AND WHERE IT WAS DRIVEN INSTEAD. That the entity's registration
// follows its files to the new drive. The second device here is inside the Cove container only: the
// instance mounts the library's volume and nothing else, so it can reach no path on that drive and
// agrees to no root there. The whole journey - the link rebuilt under the new drive's tree, the
// registration moved to it, and the old name taken back - is driven on a stack with two real drives
// both products mount, on both generations.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the
// product. A red end-to-end run in this repository is usually the Cove container dying.
import { pollUntil } from "@cove-extensions/e2e/poll";

import { expect, extensionRoute, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import {
  changedLongAgo,
  distinctMediaUnder,
  identityOf,
  ignoreFileIn,
  mediaUnder,
  namesIn,
  sharedBetweenBothProducts,
} from "../../lib/tree-steps.mjs";

/** The volume both containers reach, which the installation already declares as a library root. */
const COVE_ROOT = "/shared";

/** The installation's second library root, on a device of its own. */
const OTHER_DRIVE = "/data2";

const MOVED_TO = `${OTHER_DRIVE}/moved`;

const RUN_BUDGET_MS = 240_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

async function reconcile(api, label) {
  const started = await api.post(extensionRoute("sync/run"), { alsoMonitor: false });
  expect(
    started.json?.refusal ?? "none",
    `${label} refused before it started: ${String(started.text).slice(0, 300)}`,
  ).toBe("none");

  const run = await pollUntil(
    async () => (await api.get(extensionRoute(`job-status/${String(started.json?.jobId)}`))).json,
    (one) => /complete|fail/i.test(String(one?.status)),
    { timeoutMs: RUN_BUDGET_MS, intervalMs: 2_000, label },
  );
  expect(run?.error ?? null, `${label} faulted: ${String(run?.error)}`).toBeNull();
  return run;
}

async function fileOf(api, videoId) {
  const read = await api.get(`/api/videos/${String(videoId)}`);
  expect(read.status, `Cove would not answer about video ${String(videoId)}`).toBe(200);
  const file = (read.json?.files ?? [])[0];
  expect(file?.id, `Cove holds no file for video ${String(videoId)}`).toBeTruthy();
  return file;
}

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    test.use({ generation, ownedMedia: true });

    test("a file that changes drive leaves a name behind, and that name is taken back with the bytes it was holding", async ({
      api,
      connected,
      isolatedCove,
    }) => {
      const { adapter, owned } = connected;
      const cove = isolatedCove.container;

      await sharedBetweenBothProducts(cove, COVE_ROOT);
      await sharedBetweenBothProducts(cove, owned.folder);
      // The mount comes back root-owned after the restart the install performs, so the host cannot
      // write here until it is handed over.
      await sharedBetweenBothProducts(cove, OTHER_DRIVE);
      await cove.exec(["mkdir", "-p", MOVED_TO], { user: "root" });
      await sharedBetweenBothProducts(cove, MOVED_TO);

      // The premise, read rather than assumed. A move that stayed on one device would keep the
      // file's identity, and every assertion below would pass for a reason that is not the product.
      const [here, there] = [
        await identityOf(cove, COVE_ROOT),
        await identityOf(cove, OTHER_DRIVE),
      ];
      expect(
        here?.device === there?.device,
        `the premise did not hold: ${COVE_ROOT} and ${OTHER_DRIVE} are one device (${String(here?.device)}), so nothing below is a cross-drive move`,
      ).toBe(false);

      const treeRoot = `${COVE_ROOT}/${adapter.treeFolder}`;
      const entityFolder = `${treeRoot}/${owned.registeredAs}`;

      await reconcile(api, "the pass before the move");

      const linked = await namesIn(cove, entityFolder);
      expect(linked, `nothing was linked into ${entityFolder}`).toHaveLength(1);
      const before = await identityOf(cove, `${entityFolder}/${linked[0]}`);
      expect(before?.names, "the library's own name for the linked file is gone").toBe(2);

      // The window the removal waits out is about churn, and it is read off the file's own
      // last-changed time. A file seeded a moment ago would be kept for it, for a reason that has
      // nothing to do with this journey.
      const file = await fileOf(api, owned.video.id);
      await changedLongAgo(cove, file.path);

      const moving = await api.post("/api/files/move", {
        fileIds: [file.id],
        // Cove reads a destination without a trailing separator as a directory that does not exist.
        destinationPath: `${MOVED_TO}/`,
      });
      // The route answers a count rather than a status: a destination it will not write to is a
      // success carrying nothing moved.
      expect(
        moving.json?.moved ?? 0,
        `the move onto the other drive moved nothing: ${String(moving.text).slice(0, 300)}`,
      ).toBe(1);

      // Cove's row follows the request and the file behind it goes on the host's own schedule, so
      // one reading straight after the call is a reading of that race.
      const moved = await pollUntil(
        () => fileOf(api, owned.video.id),
        (one) => one.path.startsWith(`${OTHER_DRIVE}/`),
        { timeoutMs: 60_000, intervalMs: 1_000, label: "the library's own record of the move" },
      );
      const onTheOtherDrive = await identityOf(cove, moved.path);
      expect(
        onTheOtherDrive?.device,
        "the file did not change device, so this is not the journey this spec is about",
      ).not.toBe(before?.device);
      expect(
        (await identityOf(cove, `${entityFolder}/${linked[0]}`))?.names,
        "the name left behind is not the only one its file has, so nothing here can free any bytes",
      ).toBe(1);

      const heldBefore =
        (await distinctMediaUnder(cove, COVE_ROOT)) + (await distinctMediaUnder(cove, OTHER_DRIVE));

      await reconcile(api, "the pass after the move");

      expect(
        await namesIn(cove, entityFolder),
        `the name on ${COVE_ROOT} is still there, holding every byte of the file that left`,
      ).toEqual([]);
      expect(
        (await distinctMediaUnder(cove, COVE_ROOT)) + (await distinctMediaUnder(cove, OTHER_DRIVE)),
        "the file that was left behind is still on disk, so nothing was freed",
      ).toBe(heldBefore - 1);
      expect(
        await mediaUnder(cove, OTHER_DRIVE),
        `the library's own copy went with the name that was taken back`,
      ).toContain(moved.path);

      // Each drive keeps the host's scan out of its own tree. One ignore file cannot cover the
      // other drive, so a tree built on the second one needs its own.
      expect(
        await ignoreFileIn(cove, treeRoot),
        `the tree root ${treeRoot} carries no ignore file the host's scan will honour`,
      ).toBe("*");
    });
  });
}

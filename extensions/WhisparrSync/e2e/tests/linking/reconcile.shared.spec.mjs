// Taking back a name this extension wrote, and only that.
//
// The folder an entity has in the tree holds a second name for each of its library files. This
// drives the three things that can happen to one of those files - it is renamed, it is moved, it is
// deleted - and the one thing that can happen to the folder that is nobody's business of this
// product's: a file it did not put there.
//
// WHY A RENAME IS DRIVEN AGAINST THE FILESYSTEM. A rename by the reader, a rename by the Renamer
// and a move within one drive are one act down there: the name changes and the identity does not.
// So driving `mv` tests what driving the Renamer would, and needs no second extension installed.
// Cove's own move route is driven as well, because that is the case where the library's record
// follows the file and the product reads it at its new path.
//
// WHY THE EVIDENCE IS A SHELL. Cove cannot see the tree; the ignore file at its root is what keeps
// the host's scan out. There is no route that answers what is in there, and identity is what tells
// a second NAME for a file from a second COPY of it.
//
// WHAT THIS DOES NOT ESTABLISH. The sentence a run carries when it finds files it did not compose.
// Only the pass that registers scenes composes a linking line at all, so asserting it here would be
// a claim about one generation inside a body that runs on both. It is asserted in the backend suite
// against the line itself.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the
// product. A red end-to-end run in this repository is usually the Cove container dying.
import { attemptUntil, pollUntil } from "@cove-extensions/e2e/poll";

import { expect, extensionRoute, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import {
  changedLongAgo,
  distinctMediaUnder,
  identityOf,
  leaveAFileIn,
  linkNameOf,
  mediaUnder,
  namesIn,
  renameOnDisk,
  sharedBetweenBothProducts,
} from "../../lib/tree-steps.mjs";

/** The volume both containers reach, which the installation already declares as a library root. */
const COVE_ROOT = "/shared";

/** A second folder under the same root, so a move stays on one drive as a rename does. */
const MOVED_TO = `${COVE_ROOT}/moved`;

const RUN_BUDGET_MS = 240_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

/** Runs the library pass and waits for the host's own job record to settle. */
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

/** Where Cove holds the one file of a video, read off the host rather than assumed. */
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

    test("a rename and a move leave the links alone, a deleted file gives its bytes back, and a file this product did not write survives", async ({
      api,
      connected,
      isolatedCove,
    }) => {
      const { adapter, owned } = connected;
      const cove = isolatedCove.container;

      await sharedBetweenBothProducts(cove, COVE_ROOT);

      // Two files, because a deletion below takes one of them away and an entity that owns none is
      // no longer an entity the library pass walks.
      const second = await owned.ownAnother();
      const entityFolder = `${COVE_ROOT}/${adapter.treeFolder}/${owned.registeredAs}`;

      // Both were written moments ago, and this journey is about names rather than about the clock.
      const library = await mediaUnder(cove, COVE_ROOT);
      for (const path of library) {
        await changedLongAgo(cove, path);
      }

      // The folder the library keeps them in, as well as the root. Unlinking a name needs write
      // access to the directory holding it, and the fixture hands each entity's folder to the
      // instance's user; without this the move below is refused for a reason that has nothing to do
      // with the product.
      await sharedBetweenBothProducts(cove, library[0].slice(0, library[0].lastIndexOf("/")));

      await reconcile(api, "the first pass");

      const linked = await namesIn(cove, entityFolder);
      expect(
        linked.length,
        `nothing was linked into ${entityFolder}: ${JSON.stringify({
          libraryRoot: await namesIn(cove, COVE_ROOT),
          library: await mediaUnder(cove, COVE_ROOT),
        })}`,
      ).toBe(2);
      const filesBefore = await distinctMediaUnder(cove, COVE_ROOT);

      // A move the library's own record follows, which is what the Renamer leaves behind.
      await cove.exec(["mkdir", "-p", MOVED_TO], { user: "root" });
      await cove.exec(["chown", "cove:cove", MOVED_TO], { user: "root" });
      const moving = await api.post("/api/files/move", {
        fileIds: [(await fileOf(api, second.id)).id],
        destinationPath: MOVED_TO,
      });
      expect(
        moving.status,
        `the move was refused: ${String(moving.text).slice(0, 300)}`,
      ).toBeLessThan(300);

      await reconcile(api, "the pass after the move");

      expect(
        await namesIn(cove, entityFolder),
        "a move within one drive changed the names in the folder, so the pairing is following paths",
      ).toEqual(linked);

      // A rename the library's record does not follow, which is what a reader renaming a file in a
      // file manager leaves behind.
      const renamed = await fileOf(api, owned.video.id);
      await renameOnDisk(cove, renamed.path, `${renamed.path.slice(0, -4)} renamed.mp4`);

      await reconcile(api, "the pass after the rename");

      expect(
        await namesIn(cove, entityFolder),
        "a rename changed the names in the folder, so the pairing is following paths",
      ).toEqual(linked);
      expect(
        await distinctMediaUnder(cove, COVE_ROOT),
        "the number of files under the library root changed, so something was copied",
      ).toBe(filesBefore);

      // The reader throws one of the files away. Its link is then the only thing holding its bytes,
      // which is the state this whole capability exists to be able to end.
      const gone = await fileOf(api, second.id);
      const goneLink = linkNameOf(await identityOf(cove, gone.path), ".mp4");
      expect(linked, `no name in ${entityFolder} spells the identity of ${gone.path}`).toContain(
        goneLink,
      );
      const deleted = await api.delete(
        `/api/videos/${String(second.id)}?deleteFile=true&deleteGenerated=true`,
      );
      expect(
        deleted.status,
        `Cove would not delete the video: ${String(deleted.text).slice(0, 300)}`,
      ).toBeLessThan(300);
      // Polled: the row goes with the request and the file behind it goes on the host's own
      // schedule, so one reading straight after the call is a reading of the race.
      const { settled } = await attemptUntil(
        async (_signal, record) => {
          const read = await identityOf(cove, gone.path);
          record(read === null ? "gone" : JSON.stringify(read));
          return read === null ? { value: true } : null;
        },
        {
          timeoutMs: 60_000,
          intervalMs: 2_000,
          label: "the host deleting the file it was told to",
        },
      );
      expect(settled, `Cove left ${gone.path} where it was`).toBe(true);
      expect(
        (await identityOf(cove, `${entityFolder}/${goneLink}`))?.names,
        "the link is not the last name the deleted file has, so removing it would free nothing",
      ).toBe(1);

      await reconcile(api, "the pass after the deletion");

      expect(
        await namesIn(cove, entityFolder),
        "the link to the deleted file is still there, holding every byte of it",
      ).toEqual(linked.filter((name) => name !== goneLink));
      expect(
        await distinctMediaUnder(cove, COVE_ROOT),
        "the bytes the removed link was holding are still on the reader's disk",
      ).toBe(filesBefore - 1);

      // The case the check exists for: a file this product did not write, as a download the
      // instance imported and the placement refused leaves one. Every other reading says take it -
      // one name, no library row, and older than any window.
      const left = await leaveAFileIn(
        cove,
        entityFolder,
        "Studio.Name.-.2025-01-01.-.Scene.XXX.1080p.WEBDL.mp4",
      );

      await reconcile(api, "the pass over a folder holding a file this product did not write");

      expect(
        await identityOf(cove, left),
        "a file this product did not put in the folder was taken away, and it was the only copy",
      ).not.toBeNull();
    });
  });
}

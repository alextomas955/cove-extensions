// Monitoring an entity from this product, and following what the instance then does with it all the
// way to a file Cove holds. Nothing between the products is simulated: this product monitors, the
// instance searches, an indexer answers, a real torrent engine downloads, the instance imports what
// it downloaded, the instance tells Cove on its own, and Cove opens the file that message named.
//
// WHAT THE TWO NEIGHBOURING SPECS DO INSTEAD, and why neither covers this.
// `acquire-import.spec.mjs` proves the same chain from a grab: it picks a row out of an interactive
// release list and hands it over. That starts one step past the gesture a person makes, so it cannot
// prove that monitoring reaches an indexer at all.
// `../monitoring/search-monitored.shared.spec.mjs` presses the gesture, but against an instance
// asserted to hold no indexer and no download client, so nothing it starts can acquire anything. It
// proves the request arrived and stops there by design.
// This spec is the join: the gesture of the second against the stack of the first.
//
// WHAT MAKES IT POSSIBLE. One Docker volume, mounted by Cove, the instance and qBittorrent. The
// instance and qBit see it at /data and Cove at /shared, one filesystem on one device, so the
// completed download is where the instance expects it, the hardlink import has somewhere to land,
// and the imported file is one Cove can open. The two mounts differ on purpose: the extension takes
// the tail below the instance's root and re-roots it under each Cove library path, and a single
// shared path would resolve whether or not it re-rooted anything.
//
// WHY THE FIXTURE'S OWN STUDIO. Monitoring is addressed by Cove id, so the entity has to be one Cove
// holds and the instance can be told about. The connected fixture seeds exactly that pair, with the
// scene under it left unmonitored, which is the state this spec starts from.
//
// IF THIS GOES RED, read the diagnostics it prints before failing. They name which link broke: the
// monitor reaching the instance, the instance searching, the indexer answering, the engine
// downloading, the instance importing, or Cove opening.
import { WHISPARR_APP_USER, WHISPARR_DATA_MOUNT } from "@cove-extensions/e2e/whisparr";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { addCoveLibraryRoot } from "@cove-extensions/e2e/seed-media";

import { provisionAcquirePipeline } from "../../lib/acquire-pipeline.mjs";
import {
  cleanupStack,
  expect,
  extensionRoute,
  searching,
  test,
  whisparrActivity,
} from "../../lib/connected-fixture.mjs";
import { startFakeIndexer } from "../../lib/fake-indexer.mjs";
import { startQBittorrent } from "../../lib/qbittorrent-container.mjs";

const SPEC_BUDGET_MS = 1_800_000;

// Configured for the file rather than set inside the test. A test body runs AFTER its fixtures are
// built, so a budget raised there never covers the setup - and the setup here is a container stack.
test.describe.configure({ timeout: SPEC_BUDGET_MS });

const WHISPARR_DOWNLOADS = `${WHISPARR_DATA_MOUNT}/downloads`;

/** The same volume as the Cove container reaches it. */
const COVE_SHARED = "/shared";
const COVE_ROOT = `${COVE_SHARED}/media`;

/** The name the instance reaches Cove by, which is the compose service's own network alias. */
const COVE_ALIAS = "cove";

const SEARCH_BUDGET_MS = 120_000;
const DOWNLOAD_BUDGET_MS = 180_000;
const IMPORT_BUDGET_MS = 300_000;

/**
 * Every file path Cove holds a video for.
 *
 * The path and not the title. A video Cove registered from a file carries no title of its own until
 * a metadata source names it, and no such source is reachable from a sealed container. The path is
 * also the stronger claim: it is the one thing that proves Cove opened the file the instance
 * imported rather than some other file.
 */
async function videoFilePaths(api) {
  const answered = await api.get("/api/videos?perPage=100");
  return (answered.json?.items ?? []).flatMap((video) =>
    (video?.files ?? []).map((file) => file?.path).filter(Boolean),
  );
}

test.describe("Whisparr v2", () => {
  test.use({ generation: "v2", instanceMount: WHISPARR_DATA_MOUNT });

  test("monitoring an entity downloads its catalogue and the file reaches Cove", async ({
    isolatedCove,
    connected,
  }) => {
    const { adapter, api, instance, run, studio, whisparr } = connected;
    const container = whisparr.v2.container;

    // The indexer and the engine hold endpoints on the installation's network, so they stop before
    // it does: this stack unwinds when the body leaves, and the fixture's own unwinds after that.
    const cleanup = cleanupStack();
    try {
      await container.exec(["mkdir", "-p", WHISPARR_DOWNLOADS], { user: "root" });

      const network = isolatedCove.container.getNetworkNames()[0];
      const fakeIndexer = await startFakeIndexer({ networkName: network });
      cleanup.push("the indexer", () => fakeIndexer.stop());
      const qbit = await startQBittorrent({
        networkName: network,
        dataVolume: isolatedCove.sharedVolume,
        dataMount: WHISPARR_DATA_MOUNT,
        downloadDir: WHISPARR_DOWNLOADS,
      });
      cleanup.push("the download client", () => qbit.stop());
      await provisionAcquirePipeline({ whisparrApi: instance, fakeIndexer, qbit });

      // The Cove library path naming the same directory as the instance's root. Without it the
      // reported path re-roots under /shared alone and lands a directory above the file.
      await addCoveLibraryRoot(api, COVE_ROOT, ["/data", "/data2", COVE_SHARED]);

      // Registered through the extension's own route, so what the instance posts to is what the
      // product put there, secret and all. The address is given because the default is derived from
      // the request the browser made, and localhost inside the instance's container is the instance.
      const registered = await api.post(extensionRoute("callback/register"), {
        callbackAddress: `http://${COVE_ALIAS}:5073`,
      });
      expect(
        registered.json?.status,
        `the callback did not register: ${registered.status} ${registered.text?.slice(0, 300)}`,
      ).toBe("registered");

      // The folder the catalogue row names, which the seed writes as a column and nothing creates.
      // The instance imports into it, and a destination that is missing or root-owned gives an
      // import that reports success and attaches no file.
      // Found by the title the fixture gives both rows, because the fixture seeds the pair and
      // exposes no instance id for it. One title names one site here by construction.
      const listed = await instance.get("/api/v3/series");
      const site = (listed.json ?? []).find((one) => one.title === connected.studioName);
      expect(
        site,
        `the instance lists no site titled ${connected.studioName}: ${JSON.stringify(listed.json ?? []).slice(0, 300)}`,
      ).toBeTruthy();
      await container.exec(["mkdir", "-p", site.path], { user: "root" });
      await container.exec(["chown", "-R", WHISPARR_APP_USER, WHISPARR_DATA_MOUNT], {
        user: "root",
      });

      // Cove is deliberately left holding nothing for this scene, so a video it holds at the end can
      // only have come from the file it opened.
      expect(
        await videoFilePaths(api),
        "Cove already held a video before anything was monitored",
      ).toEqual([]);

      // The bound every claim below rests on: the instance had not been asked to search before the
      // gesture, so a searching command found afterwards is attributable to it.
      const before = await whisparrActivity(instance);
      expect(
        searching(before.commandNames),
        `the instance had already been asked to search: ${before.commandNames.join(", ")}`,
      ).toEqual([]);

      // The gesture. Monitoring the whole catalogue rather than future scenes only, because a scene
      // already released is the one this stack can answer for.
      const monitored = await api.post(
        extensionRoute(`entity/studio/${String(studio.id)}/monitor`),
        { scope: "allScenes" },
      );
      expect(
        monitored.status,
        `the monitor was refused: ${monitored.status} ${monitored.text?.slice(0, 400)}`,
      ).toBeLessThan(400);

      // The instance's own roster, never this product's answer. The route answers a monitoring view
      // rather than a job, so its reply says nothing about whether the request arrived.
      await pollUntil(
        async () => searching((await whisparrActivity(instance)).commandNames),
        (names) => names.length > 0,
        {
          timeoutMs: SEARCH_BUDGET_MS,
          intervalMs: 2000,
          label: "the instance is asked to search for what it now monitors",
        },
      );

      // The engine really downloads it. With a webseed and no peers this is an HTTP fetch, but it is
      // qBittorrent doing it from a torrent the instance handed over on its own.
      let completed;
      try {
        completed = await pollUntil(
          () => qbit.torrents(),
          (torrents) => torrents.length > 0 && torrents[0].progress === 1,
          {
            timeoutMs: DOWNLOAD_BUDGET_MS,
            intervalMs: 2000,
            label: "qBittorrent completes a download the monitor started",
          },
        );
      } catch (failure) {
        // Three links look the same from here: the search asking no indexer, the indexer answering
        // nothing, and the instance rejecting what it got back. The history names which.
        const history = await instance.get("/api/v3/history?pageSize=20");
        console.error(
          "MONITOR DIAGNOSTIC history:",
          JSON.stringify(
            (history.json?.records ?? []).map((record) => ({
              event: record.eventType,
              source: record.sourceTitle,
              data: record.data,
            })),
          ).slice(0, 2000),
        );
        const direct = await fetch(`${fakeIndexer.urlFromHost}/api?t=search&q=test`);
        console.error(
          "MONITOR DIAGNOSTIC indexer serves:",
          direct.status,
          (await direct.text()).slice(0, 300),
        );
        throw failure;
      }
      expect(completed[0].progress, "the torrent did not reach 100%").toBe(1);

      // The catalogue entry reporting that it holds a file is what proves the import, read off the
      // instance's own rows rather than the history.
      const entryId = site.id;
      try {
        await pollUntil(
          () => adapter.ownedEntryHoldsFile(instance, entryId),
          (holds) => holds === true,
          {
            timeoutMs: IMPORT_BUDGET_MS,
            intervalMs: 2000,
            label: "the instance imports the completed download and attaches the file",
          },
        );
      } catch (failure) {
        const queue = await instance.get("/api/v3/queue?pageSize=20");
        console.error(
          "MONITOR DIAGNOSTIC queue:",
          JSON.stringify(
            (queue.json?.records ?? []).map((record) => ({
              status: record.status,
              state: record.trackedDownloadState,
              tracked: record.trackedDownloadStatus,
              messages: (record.statusMessages ?? []).flatMap((message) => [
                message.title,
                ...(message.messages ?? []),
              ]),
            })),
          ),
        );
        throw failure;
      }

      // Where the instance says it put the file. That path is what its notification carries, and the
      // one the extension re-roots under a Cove library path.
      const importedRows = await adapter.ownedFileRows(instance, entryId);
      const whisparrPath = importedRows[0]?.path;
      expect(
        whisparrPath,
        `the instance reports no file for the catalogue entry: ${JSON.stringify(importedRows).slice(0, 300)}`,
      ).toBeTruthy();

      // The claim this spec exists for. The instance raised its own notification because of a
      // monitor this product made, Cove received it, and Cove now holds the file.
      const coveePath = whisparrPath.replace(WHISPARR_DATA_MOUNT, COVE_SHARED);
      await pollUntil(
        () => videoFilePaths(api),
        (paths) => paths.includes(coveePath),
        {
          timeoutMs: IMPORT_BUDGET_MS,
          intervalMs: 2000,
          label: `Cove holds the file the instance imported, at ${coveePath}`,
        },
      );

      expect(
        await videoFilePaths(api),
        `Cove holds no video at ${coveePath} after run ${run}`,
      ).toContain(coveePath);
    } finally {
      await cleanup.unwind();
    }
  });
});

// The whole acquire chain, with nothing between the products simulated: an indexer answers, a real
// torrent engine downloads, Whisparr imports what it downloaded, Whisparr tells Cove on its own, and
// Cove opens the file that message named. One journey per generation, each against an installation
// of its own.
//
// WHAT THE REST OF THE SUITE DOES INSTEAD. `import-webhook.shared.spec.mjs` composes a delivery body
// itself and posts it. That proves what Cove does with a delivery; it cannot prove that Whisparr
// sends one, that the body it sends is the body the extension reads, or that the path it names
// resolves to a file Cove can open. Nothing here writes a delivery body or a torrent peer.
//
// WHY A DOWNLOAD CLIENT AND NOT A HAND-PLACED FILE. Measured, not assumed: telling Whisparr to import
// a file already on disk does import it, and raises no `Download` event. The extension acts on that
// one event, so a hand-placed file reaches Cove as a delivery it correctly ignores. Only an import
// that came from a download client produces the event the product listens for.
//
// WHAT MAKES IT POSSIBLE. One Docker volume, mounted by Cove, Whisparr and qBittorrent. Whisparr and
// qBit see it at /data and Cove at /shared, one filesystem on one device, so the completed download is
// where Whisparr expects it, the hardlink import has somewhere to land, and the imported file is one
// Cove can open.
//
// THE PATH MAPPING UNDER TEST, and why the two mounts differ on purpose. Whisparr reports the path it
// imported to, under its own root. The extension takes the tail below that root and re-roots it under
// each Cove library path, then opens whichever candidate is there. Mounting the volume at one path in
// both containers would make a reported path resolve whether or not the extension re-rooted anything.
// /shared/media is added as a Cove library path because it names the same directory as Whisparr's own
// root.
//
// WHERE THE DOWNLOAD LANDS, and why it must not stay there. The entry is registered at the folder
// this extension keeps for it, so Whisparr imports the completed download into that folder, which is
// inside the tree. The tree is invisible to Cove's own scan by design, so an item whose only file
// sits there is outside the reader's layout and beyond anything a rescan would find again. The
// extension gives the same bytes a second name under the library root and registers that one.
//
// WHY BOTH PRODUCTS ARE GIVEN THE ROOT. Whisparr owns what it downloads and Cove makes the second
// name, so both write the same directory. The fixture hands the volume to the instance's user, which
// leaves Cove unable to create anything and the placement refused for a reason that has nothing to
// do with the product.
//
// IF THIS GOES RED, read the diagnostics it prints before failing. They name which link broke: the
// indexer answering, the engine downloading, Whisparr importing, the extension placing, or Cove
// opening.
import { WHISPARR_APP_USER, WHISPARR_DATA_MOUNT } from "@cove-extensions/e2e/whisparr";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { addCoveLibraryRoot } from "@cove-extensions/e2e/seed-media";

import { provisionAcquirePipeline, seedAcquirableScene } from "../../lib/acquire-pipeline.mjs";
import { cleanupStack, expect, extensionRoute, test } from "../../lib/connected-fixture.mjs";
import { startFakeIndexer } from "../../lib/fake-indexer.mjs";
import { startQBittorrent } from "../../lib/qbittorrent-container.mjs";
import { identityOf, sharedBetweenBothProducts } from "../../lib/tree-steps.mjs";

const SPEC_BUDGET_MS = 1_800_000;

// Configured for the file rather than set inside each test. A test body runs AFTER its fixtures are
// built, so a budget raised there never covers the setup - and the setup here is a container stack,
// which is the slowest part and the part that outruns the default when the machine is loaded.
test.describe.configure({ timeout: SPEC_BUDGET_MS });

/** Whisparr's root folder and the download directory, both on the shared volume. */
const WHISPARR_ROOT = `${WHISPARR_DATA_MOUNT}/media`;
const WHISPARR_DOWNLOADS = `${WHISPARR_DATA_MOUNT}/downloads`;

/** The same volume as the Cove container reaches it. */
const COVE_SHARED = "/shared";
const COVE_ROOT = `${COVE_SHARED}/media`;

/**
 * The name Whisparr reaches Cove by, which is the compose service's own network alias.
 *
 * Not the mapped host port the test process uses: that is published on the host, and inside a
 * container `localhost` is the container itself.
 */

const DOWNLOAD_BUDGET_MS = 180_000;
const IMPORT_BUDGET_MS = 300_000;

/**
 * Every file path Cove holds a video for.
 *
 * The path and not the title. A video Cove registered from a file carries no title of its own until a
 * metadata source names it, and no such source is reachable from a sealed container, so every title
 * here is null. The path is also the stronger claim: it is the one thing that proves Cove opened the
 * file Whisparr imported rather than some other file.
 */
async function videoFilePaths(api) {
  const answered = await api.get("/api/videos?perPage=100");
  return (answered.json?.items ?? []).flatMap((video) =>
    (video?.files ?? []).map((file) => file?.path).filter(Boolean),
  );
}

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    // Only this generation's installation and instance, with the library's own volume under the
    // instance's catalogue root at the instance's own path.
    test.use({ generation, instanceMount: WHISPARR_DATA_MOUNT });

    test("a grab downloads through a real engine, imports, and reaches Cove on Whisparr's own word", async ({
      isolatedCove,
      connected,
    }) => {
      const { adapter, api, instance, run, whisparr } = connected;
      const container = whisparr[generation].container;
      const cove = isolatedCove.container;

      // The indexer and the engine hold endpoints on the installation's network, so they stop
      // before it does: this stack unwinds when the body leaves, and the fixture's own unwinds
      // after that.
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

        // The Cove library path naming the same directory as Whisparr's root. Without it the
        // reported path re-roots under /shared alone and lands a directory above the file.
        await addCoveLibraryRoot(api, COVE_ROOT, ["/data", "/data2", COVE_SHARED]);

        // Registered through the extension's own route, so what Whisparr posts to is what the
        // product put there, secret and all. The address is given because the default is derived
        // from the request the browser made, and localhost inside the Whisparr container is
        // Whisparr.
        const registered = await api.post(extensionRoute("callback/register"), {
          callbackAddress: isolatedCove.internalBaseUrl,
        });
        expect(
          registered.json?.status,
          `the callback did not register: ${registered.status} ${registered.text?.slice(0, 300)}`,
        ).toBe("registered");

        // The tree, made before the entry is moved into it: the instance refuses a root folder that
        // is not there, and a folder the instance cannot write is an import that reports success and
        // attaches no file.
        const treeRoot = `${WHISPARR_ROOT}/${adapter.treeFolder}`;
        await container.exec(["mkdir", "-p", treeRoot], { user: "root" });
        await container.exec(["chown", "-R", WHISPARR_APP_USER, WHISPARR_DATA_MOUNT], {
          user: "root",
        });

        const target = await seedAcquirableScene({
          generation,
          whisparr,
          rootFolder: WHISPARR_ROOT,
          treeRoot,
          run,
        });

        // The folder the catalogue row names, which is a column and not a directory until now.
        await container.exec(["mkdir", "-p", target.folder], { user: "root" });
        await container.exec(["chown", "-R", WHISPARR_APP_USER, WHISPARR_DATA_MOUNT], {
          user: "root",
        });
        // After the ownership pass, which would otherwise take the root back off Cove.
        await sharedBetweenBothProducts(isolatedCove.container, COVE_ROOT);

        // Cove is deliberately left holding nothing for this scene, so a video it holds at the end
        // can only have come from the file it opened.
        expect(
          await videoFilePaths(api),
          "Cove already held a video before anything was downloaded",
        ).toEqual([]);

        // The premise of everything below: the instance imports a completed download under the
        // folder its entry names, so an entry that is no longer in the tree measures the ordinary
        // import path and says nothing about a placement. Under the tree rather than at one exact
        // folder, because one generation composes a folder of its own below the root it is given.
        expect(
          await adapter.entryPath(instance, target.entryId),
          "the instance no longer holds the entry inside the tree",
        ).toContain(`/${adapter.treeFolder}/`);

        // The interactive release list, then a grab of one row from it. The automatic search is not
        // used: it applies match and quality gates a synthetic release cannot satisfy, and reports
        // an unsuccessful result naming no reason. Picking a row is also exactly how a person grabs
        // one.
        let releases;
        try {
          releases = await pollUntil(
            async () => (await instance.get(`/api/v3/release?${target.releaseQuery}`)).json,
            (rows) => Array.isArray(rows) && rows.length > 0,
            { timeoutMs: 120_000, label: "the indexer answers with a release for the scene" },
          );
        } catch (failure) {
          // Three different faults look the same from here: the stub not serving, Whisparr unable
          // to reach it, and Whisparr reaching it and dropping the item it got back.
          const direct = await fetch(`${fakeIndexer.urlFromHost}/api?t=search&q=test`);
          console.error(
            "ACQUIRE DIAGNOSTIC indexer serves:",
            direct.status,
            (await direct.text()).slice(0, 300),
          );
          const listed = await instance.get("/api/v3/indexer");
          console.error(
            "ACQUIRE DIAGNOSTIC indexer row:",
            JSON.stringify(
              (listed.json ?? []).map((one) => ({
                name: one.name,
                enableInteractiveSearch: one.enableInteractiveSearch,
                enableAutomaticSearch: one.enableAutomaticSearch,
                fields: (one.fields ?? [])
                  .filter((f) => ["baseUrl", "apiPath", "categories"].includes(f.name))
                  .map((f) => [f.name, f.value]),
              })),
            ),
          );
          const tested = await instance.post("/api/v3/indexer/test", listed.json?.[0] ?? {});
          console.error(
            "ACQUIRE DIAGNOSTIC indexer test:",
            tested.status,
            tested.text?.slice(0, 400),
          );
          throw failure;
        }
        const grabbed = await instance.post("/api/v3/release", {
          ...releases[0],
          ...target.grabFields,
        });
        expect(grabbed.status, `the grab was refused: ${grabbed.text?.slice(0, 400)}`).toBeLessThan(
          400,
        );

        // The engine really downloads it. With a webseed and no peers this is an HTTP fetch, but it
        // is qBittorrent doing it from a torrent it was handed.
        const completed = await pollUntil(
          () => qbit.torrents(),
          (torrents) => torrents.length > 0 && torrents[0].progress === 1,
          {
            timeoutMs: DOWNLOAD_BUDGET_MS,
            intervalMs: 2000,
            label: "qBittorrent completes the download",
          },
        );
        expect(completed[0].progress, "the torrent did not reach 100%").toBe(1);

        // The catalogue entry reporting that it holds a file is what proves the import, read off the
        // instance's own rows rather than the history.
        try {
          await pollUntil(
            () => adapter.ownedEntryHoldsFile(instance, target.entryId),
            (holds) => holds === true,
            {
              timeoutMs: IMPORT_BUDGET_MS,
              intervalMs: 2000,
              label: "Whisparr imports the completed download and attaches the file",
            },
          );
        } catch (failure) {
          const queue = await instance.get("/api/v3/queue?pageSize=20");
          console.error(
            "ACQUIRE DIAGNOSTIC queue:",
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

        // Where Whisparr says it put the file. That path is what its notification carries, and the
        // one the extension re-roots under a Cove library path.
        const importedRows = await adapter.ownedFileRows(instance, target.entryId);
        const whisparrPath = importedRows[0]?.path;
        expect(
          whisparrPath,
          `Whisparr reports no file for the catalogue entry: ${JSON.stringify(importedRows).slice(0, 300)}`,
        ).toBeDefined();

        // The two names the same bytes end up under, composed here rather than read back from the
        // extension, so these are expectations and not restatements of whatever it resolved to. The
        // first is the tree path the instance goes on recording; the second is where the reader
        // keeps their files, which is the top of the library root the tree sits under.
        const arrivalAsCoveReachesIt = whisparrPath.replace(WHISPARR_DATA_MOUNT, COVE_SHARED);
        const expectedCovePath = `${COVE_ROOT}/${arrivalAsCoveReachesIt.split("/").pop()}`;

        // The claim this spec exists for. Whisparr raised its own notification, Cove received it,
        // resolved the path it named under a library root of its own, and opened the file that was
        // there.
        try {
          const held = await pollUntil(
            () => videoFilePaths(api),
            (paths) => paths.length > 0,
            {
              timeoutMs: IMPORT_BUDGET_MS,
              intervalMs: 2000,
              label: "Cove holds a video for the file Whisparr imported",
            },
          );

          expect(
            held.length,
            `Cove holds more than the one imported file: ${held.join(", ")}`,
          ).toBe(1);
          expect(held[0], "Cove registered a file other than the one it placed").toBe(
            expectedCovePath,
          );
          expect(
            held[0],
            "Cove registered the file inside the tree, where no rescan would find it again",
          ).not.toContain(`/${adapter.treeFolder}/`);

          // One file under two names. The instance goes on recording the arrival where it put it,
          // so that name has to still be there, and a second copy of the bytes would read as a
          // different file rather than as a second name for this one.
          const arrival = await identityOf(cove, arrivalAsCoveReachesIt);
          const placed = await identityOf(cove, held[0]);
          expect(
            arrival,
            `the path the instance recorded resolves to nothing: ${arrivalAsCoveReachesIt}`,
          ).not.toBeNull();
          expect(
            { device: placed?.device, number: placed?.number },
            "the file Cove registered is a different file from the one Whisparr imported, so the bytes were copied",
          ).toEqual({ device: arrival?.device, number: arrival?.number });
          // At least two, because the download client's own name for the completed download is
          // still there: the instance's import is itself a hard link.
          expect(
            placed?.names,
            "the download carries one name, so the placement replaced a name rather than adding one",
          ).toBeGreaterThanOrEqual(2);

          // Tied to the delivery, not merely to the file being there. Cove scans its own library
          // paths, so a video at that path is on its own consistent with a scan having found it and
          // no notification ever arriving. This is the extension recording that one presented its
          // secret.
          const delivered = await api.get(extensionRoute("callback/status"));
          expect(
            delivered.json?.lastEventSecretPosition,
            "no delivery ever reached the extension, so the file was registered by something else",
          ).not.toBeNull();
        } catch (failure) {
          // Which half broke. The callback status says whether a delivery ever arrived, which tells
          // a notification Whisparr never sent from one Cove received and could not act on.
          const status = await api.get(extensionRoute("callback/status"));
          console.error(
            "ACQUIRE DIAGNOSTIC callback:",
            JSON.stringify({
              status: status.json?.status,
              lastEventSecretPosition: status.json?.lastEventSecretPosition,
            }),
          );
          const banner = await api.get(extensionRoute("import/banner"));
          console.error("ACQUIRE DIAGNOSTIC banner:", banner.text?.slice(0, 700));
          const where = await container.exec([
            "sh",
            "-c",
            `find ${WHISPARR_DATA_MOUNT} -type f -printf '%i %p\n' 2>/dev/null || find ${WHISPARR_DATA_MOUNT} -type f`,
          ]);
          console.error("ACQUIRE DIAGNOSTIC files:", where.output.trim());
          console.error("ACQUIRE DIAGNOSTIC imported path:", whisparrPath);

          // The two links this journey added: the arrival as Cove reaches it, and the name the
          // placement was to make. A null on the first is an import that landed somewhere else; a
          // null on the second with the first there is a placement that was refused.
          console.error(
            "ACQUIRE DIAGNOSTIC placement:",
            JSON.stringify({
              registeredAt: await adapter.entryPath(instance, target.entryId),
              arrival: await identityOf(cove, arrivalAsCoveReachesIt),
              placed: await identityOf(cove, expectedCovePath),
            }),
          );

          const owners = await container.exec([
            "sh",
            "-c",
            `id; ls -la ${WHISPARR_DATA_MOUNT} ${WHISPARR_ROOT} 2>&1`,
          ]);
          console.error("ACQUIRE DIAGNOSTIC ownership:", owners.output.trim().slice(0, 900));

          const logs = await container.exec([
            "sh",
            "-c",
            "grep -hiE 'Nullable|NotificationService|Cove Whisparr Sync|OnDownload|webhook' /config/logs/*.txt 2>/dev/null | tail -n 20",
          ]);
          console.error("ACQUIRE DIAGNOSTIC log:", logs.output.trim().slice(0, 2000));

          // What Cove made of the delivery. The extension reports a contained failure with
          // structured diagnostics, and an ignored event with nothing at all, so the absence of a
          // line here is itself the answer: the event was not one this product acts on.
          const coveLog = await isolatedCove.container.exec([
            "sh",
            "-c",
            "grep -hE 'WhisparrSync.WhisparrSync|ERR|WRN' /config/logs/*.log /config/logs/*.txt 2>/dev/null | tail -n 20",
          ]);
          console.error("ACQUIRE DIAGNOSTIC cove log:", coveLog.output.trim().slice(0, 1800));

          const raw = await api.get("/api/videos?perPage=100");
          console.error("ACQUIRE DIAGNOSTIC videos raw:", raw.status, raw.text?.slice(0, 400));
          throw failure;
        }
      } finally {
        await cleanup.unwind();
      }
    });
  });
}

// One Cove installation per test, with one Whisparr generation connected to it, addressed by both
// the browser and the API client.
//
// WHY BOTH OVERRIDES. The `page` fixture resolves its address through `baseUrl`, and the shared
// `api` fixture is wired to the worker-shared harness independently of it. Overriding one alone
// leaves the browser and the client on different Cove instances, and both answer, so a spec reads
// one database and asserts against another.
//
// WHY THE GENERATION IS AN OPTION. Which generation is connected is a global extension setting, so
// two executions of one scenario cannot share an installation. An option is also the only thing a
// fixture can read: a variable a spec's own loop closes over is invisible to the fixture that has to
// decide which container to start.
//
// WHY NOT THE `whisparr` FIXTURE. It destructures the worker harness, so naming it starts a second
// Cove pair beside the isolated one. This file calls startWhisparr directly instead.
import { randomUUID } from "node:crypto";

import { createApiClient, isolatedHarnessFixture } from "@cove-extensions/e2e";
import { seedVideo } from "@cove-extensions/e2e/seed-media";
import { startWhisparr, WHISPARR_APP_USER } from "@cove-extensions/e2e/whisparr";

import { adapterFor } from "./generation-adapter.mjs";
import { startMetadataStub } from "./metadata-stub.mjs";
import { SCENE_RELEASE_DATE, seedV2Scene } from "./seed-scene.mjs";
import {
  connectWhisparr,
  seedCoveStudio,
  STASHDB_ENDPOINT,
  test as base,
  WHISPARR_ROOT,
  WHISPARR_SYNC_EXTENSION,
  whisparrEntity,
} from "./whisparr-sync-fixtures.mjs";

/**
 * The budget a spec taking these fixtures runs under.
 *
 * Configured per describe rather than inside a test body. A body runs after its fixtures are built,
 * so a budget raised there never covers the container stack, which is the slowest part of the setup
 * and the part that outruns the default on a loaded machine.
 */
export const SPEC_BUDGET_MS = 900_000;

/**
 * Collects a stop per resource and unwinds them newest first.
 *
 * Each stop is registered the moment its resource is up, rather than in one block after the last of
 * them. A start that fails partway otherwise strands everything before it until Ryuk reaps it, and
 * enough stranded containers in one run exhaust Docker's address pool with a failure that names
 * neither this fixture nor the one that broke.
 *
 * Newest first because a Whisparr instance holds an endpoint on the network its Cove created, and
 * the daemon refuses to remove a network that still has one attached.
 */
export function cleanupStack() {
  const registered = [];
  return {
    push(label, stop) {
      registered.push({ label, stop });
    },
    async unwind() {
      // Never throws. It runs in a `finally`, and a teardown error raised over a failing body would
      // replace the failure a reader needs with this one. The label is what says which resource is
      // still up.
      for (const { label, stop } of [...registered].reverse()) {
        try {
          await stop();
        } catch (cause) {
          console.error(`[cleanup] ${label} did not stop: ${cause?.message ?? String(cause)}`);
        }
      }
    },
  };
}

/** The instance's own row for a site, which is where a v2 monitored flag is really read. */
export async function siteRow(whisparrApi, seriesId) {
  const listed = await whisparrApi.get("/api/v3/series");
  return (listed.json ?? []).find((one) => one.id === seriesId);
}

/** One title for the pair the fixture seeds, so the Cove row and the instance row name one studio. */
const studioTitle = (run) => `Cove E2E Studio ${run}`;

/**
 * Registers one file with Cove inside a folder the instance also reaches, and puts it under the
 * studio.
 *
 * Registered rather than merely placed on disk: a verb that hands the instance what the library owns
 * reads the folders of the video FILES an entity holds, so a file the host does not know about
 * leaves it with no folder to offer and it hands over nothing while reporting no failure.
 *
 * The chown is the instance's own: it reads and links as its user, and a file Cove placed arrives
 * owned by root.
 */
async function ownFile({
  api,
  isolatedCove,
  instanceContainer,
  studio,
  destDir,
  destName,
  identity,
}) {
  const video = await seedVideo({
    container: isolatedCove.container,
    baseUrl: isolatedCove.baseUrl,
    token: isolatedCove.token,
    destDir,
    destName,
  });
  const owned = await api.put(`/api/videos/${String(video.id)}`, {
    studioId: studio.id,
    ...(identity === undefined ? {} : { remoteIds: [identity] }),
  });
  if (owned.status >= 300) {
    throw new Error(
      `ownFile: putting the seeded video under the studio answered ${String(owned.status)}: ${String(owned.text).slice(0, 300)}`,
    );
  }
  await instanceContainer.exec(["chown", "-R", WHISPARR_APP_USER, isolatedCove.sharedPath], {
    user: "root",
  });
  return video;
}

/**
 * How each generation's instance is brought up and its catalogue put in front of the extension.
 *
 * A seeder starts the instance and seeds its catalogue, and hands back the identity Cove has to
 * carry plus the read that answers what the instance now holds. It never presses anything: the
 * gesture under test belongs to the spec, and evidence read off a route this product owns would
 * hold against a route that answered and wrote nothing. The spellings and state reads the two
 * generations differ over live in generation-adapter.mjs, exposed below as `adapter`.
 */
const SEEDERS = {
  v3: {
    async seedInstance({ network, run, cleanup, media }) {
      const whisparr = await startWhisparr({
        network,
        generations: ["v3"],
        ...media.start,
      });
      cleanup.push("the v3 instance", () => whisparr.stop());

      const foreignId = `cove-e2e-studio-${run}`;
      await whisparr.seedEntity("v3", {
        kind: "studio",
        foreignId,
        title: studioTitle(run),
      });

      return {
        whisparr,
        remoteId: foreignId,
        monitored: async (instance) =>
          (await whisparrEntity(instance, "studio", foreignId))?.monitored,

        // The catalogue entry the file has to land on. The instance attaches a file to an entry it
        // already holds, so with none for this scene it matches the file to nothing and reports a
        // clean pass having attached none.
        async ownMedia({ api, isolatedCove, studio }) {
          const sceneRemoteId = `cove-e2e-owned-scene-${run}`;
          const scene = await whisparr.seedEntity("v3", {
            kind: "scene",
            foreignId: sceneRemoteId,
            title: `Cove E2E Owned Scene ${run}`,
            monitored: true,
          });
          await ownFile({
            api,
            isolatedCove,
            instanceContainer: whisparr.v3.container,
            studio,
            destDir: scene.path,
            destName: `Cove E2E Owned Scene ${run} 1080p WEBDL.mp4`,
            identity: { endpoint: STASHDB_ENDPOINT, remoteId: sceneRemoteId },
          });
          return { entryId: scene.id, folder: scene.path };
        },
      };
    },
  },

  v2: {
    async seedInstance({ network, run, cleanup, media }) {
      const siteId = Math.floor(Math.random() * 500_000) + 1;

      // Started before the instance: the element naming it is read out of the config at startup and
      // never again. Without it every identifier resolves against a hosted service no sealed run
      // reaches, and the entity read answers that the instance refused.
      const metadata = await startMetadataStub({
        networkName: network,
        sites: [{ tvdbId: siteId, title: studioTitle(run), titleSlug: String(siteId) }],
      });
      cleanup.push("the v2 metadata stub", () => metadata.stop());

      const whisparr = await startWhisparr({
        network,
        generations: ["v2"],
        metadataUrl: metadata.urlFromWhisparr,
        ...media.start,
      });
      cleanup.push("the v2 instance", () => whisparr.stop());

      const seeded = await seedV2Scene(whisparr.v2.container, whisparr.apiFor("v2"), {
        siteId,
        siteTitle: studioTitle(run),
        rootFolderPath: whisparr.v2.rootFolder,
        sceneExternalId: randomUUID(),
        sceneTitle: `Scene ${run}`,
        monitored: false,
      });

      return {
        whisparr,
        remoteId: String(siteId),
        monitored: async (instance) => (await siteRow(instance, seeded.seriesId))?.monitored,

        // Under the site's own folder, and named in the shape this generation parses a release in -
        // "Site - Date - Title" with a quality the parse recognises. A name it cannot parse lists as
        // a row matched to nothing, which this product excludes, and the run then reports a clean
        // pass that attached nothing.
        async ownMedia({ api, isolatedCove, studio }) {
          const instance = whisparr.apiFor("v2");
          const site = await siteRow(instance, seeded.seriesId);
          await ownFile({
            api,
            isolatedCove,
            instanceContainer: whisparr.v2.container,
            studio,
            destDir: site.path,
            destName: `${site.title} - ${SCENE_RELEASE_DATE} - Owned ${run} 1080p WEBDL.mp4`,
          });
          return { entryId: seeded.seriesId, folder: site.path };
        },
      };
    },
  },
};

/**
 * Where the instance's catalogue is rooted, and whether the library's own volume is under it.
 *
 * A spec that only reads the instance's rows needs no volume, and mounting one would cost every
 * such spec a bind mount it never touches. A spec driving a verb that hands the instance a file the
 * library owns needs ONE filesystem reachable at ONE path from both containers: an instance mounting
 * Cove's volume anywhere else is handed a path it cannot read, links nothing, and the run still
 * completes reporting no failure.
 */
const mediaFor = (ownedMedia, isolatedCove) =>
  ownedMedia
    ? {
        start: {
          rootFolder: `${isolatedCove.sharedPath}/media`,
          dataVolume: isolatedCove.sharedVolume,
          dataMount: isolatedCove.sharedPath,
        },
      }
    : { start: { rootFolder: WHISPARR_ROOT } };

export const test = base.extend({
  generation: ["v3", { option: true }],

  // Off by default: only a spec driving a verb over a file the library owns needs the volume, and
  // the mount is not free for the specs that do not.
  ownedMedia: [false, { option: true }],

  isolatedCove: isolatedHarnessFixture(WHISPARR_SYNC_EXTENSION),

  baseUrl: async ({ isolatedCove }, use) => {
    await use(isolatedCove.baseUrl);
  },

  // Both read through the handle as getters, for the reason createApiClient documents: a restart
  // re-mints the token and can republish the container on a different host port.
  api: async ({ isolatedCove }, use) => {
    await use(
      createApiClient(
        () => isolatedCove.baseUrl,
        () => isolatedCove.token,
      ),
    );
  },

  /**
   * The installation with one instance of `generation` connected to it, and one Cove studio that
   * instance can identify, held in its catalogue and not monitored.
   *
   * Separate from `isolatedCove` so a spec needing only the installation names that one and pays for
   * no instance: Playwright builds fixtures lazily, by name.
   */
  connected: async ({ isolatedCove, api, generation, ownedMedia }, use) => {
    const seeder = SEEDERS[generation];
    if (seeder === undefined) {
      throw new Error(`connected: no seeder is written for the generation "${generation}".`);
    }

    const cleanup = cleanupStack();
    try {
      const run = randomUUID().slice(0, 8);
      const adapter = adapterFor(generation);
      const seeded = await seeder.seedInstance({
        network: isolatedCove.container.getNetworkNames()[0],
        run,
        cleanup,
        media: mediaFor(ownedMedia, isolatedCove),
      });
      const instance = seeded.whisparr.apiFor(generation);

      const studioName = studioTitle(run);
      const studio = await seedCoveStudio(api, {
        name: studioName,
        remoteIds: [{ endpoint: adapter.identityEndpoint, remoteId: seeded.remoteId }],
      });

      // Before the connection, so the extension's first read of the library already sees it.
      const owned = ownedMedia ? await seeded.ownMedia({ api, isolatedCove, studio }) : null;

      await connectWhisparr(api, seeded.whisparr, generation);

      await use({
        adapter,
        api,
        generation,
        instance,
        owned,
        remoteId: seeded.remoteId,
        run,
        studio,
        studioName,
        studioMonitored: () => seeded.monitored(instance),
        whisparr: seeded.whisparr,
      });
    } finally {
      await cleanup.unwind();
    }
  },
});

export {
  connectWhisparr,
  expect,
  EXTENSION_ID,
  extensionRoute,
  seedCovePerformer,
  seedCoveStudio,
  seedCoveVideo,
  SETTLE_DWELL_MS,
  STASHDB_ENDPOINT,
  THEPORNDB_ENDPOINT,
  whisparrAcquisitionSurface,
  whisparrActivity,
  whisparrEntity,
  WHISPARR_ROOT,
} from "./whisparr-sync-fixtures.mjs";

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
import { randomInt, randomUUID } from "node:crypto";

import { createApiClient } from "@cove-extensions/e2e";
import { seedVideo } from "@cove-extensions/e2e/seed-media";
import { startWhisparr, WHISPARR_APP_USER } from "@cove-extensions/e2e/whisparr";

import { adapterFor } from "./generation-adapter.mjs";
import { startMetadataStub } from "./metadata-stub.mjs";
import { configureProviderStub, startProviderStub, STASHDB_STUB_SERVER } from "./provider-stub.mjs";
import { startThePornDbStub, THEPORNDB_STUB_SERVER } from "./theporndb-stub.mjs";
import { SCENE_RELEASE_DATE, seedV2Scene } from "./seed-scene.mjs";
import {
  connectWhisparr,
  seedCoveStudio,
  STASHDB_ENDPOINT,
  test as base,
  WHISPARR_ROOT,
  isolatedCoveFixture,
  whisparrEntity,
} from "./whisparr-sync-fixtures.mjs";

/**
 * The budget a spec taking these fixtures runs under.
 *
 * Configured per describe rather than inside a test body. A body runs after its fixtures are built,
 * so a budget raised there never covers the container stack, which is the slowest part of the setup
 * and the part that outruns the default on a loaded machine.
 *
 * Wide enough for the slowest spec and no wider. The slowest single test measured is about a
 * minute, on the two-worker runner as well as locally, and the longest wait any one spec is allowed
 * is four minutes. A budget far above that does not make a slow spec pass; it only decides how long
 * a wedged one sits before it reports. A locator naming an element that no longer exists never
 * resolves, and the run's whole job is what pays for the wait.
 */
export const SPEC_BUDGET_MS = 300_000;

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
 * How many entities a seeded layout holds, and how many files each of them owns.
 *
 * More than one entity, because a layout is about several entities' files sharing one folder. More
 * than one file each, because an entity owning a single file cannot show that its folder in the
 * tree holds a name per file it owns.
 */
const LAYOUT_ENTITIES = 2;
const LAYOUT_FILES = 2;

/** One title for a layout's entity, so the Cove studio and the instance's own row name one thing. */
const layoutTitle = (run, index) => `Cove E2E Layout ${run} ${String(index)}`;

/** Where a layout's files sit, in the spelling of whichever container `root` is read from. */
const layoutFolderIn = (root, layout) =>
  layout.folder === null ? `${root}/media` : `${root}/media/${layout.folder}`;

/**
 * What one seeded file is called: the layout's own name where it states one, and otherwise the
 * caller's spelling for the generation being seeded.
 */
function layoutFileName(layout, index, file, named) {
  if (layout.names === null) return named;

  const at = index * LAYOUT_FILES + file;
  if (at >= layout.names.length) {
    throw new Error(
      `connected: the layout states ${String(layout.names.length)} file name(s), and seeding ${String(LAYOUT_ENTITIES)} entities holding ${String(LAYOUT_FILES)} files each needs ${String(LAYOUT_ENTITIES * LAYOUT_FILES)}.`,
    );
  }
  return layout.names[at];
}

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
 *
 * `destDir` is the INSTANCE's spelling, because it comes out of the instance's own catalogue. Where
 * the two containers mount the volume at different paths, the file has to be written and registered
 * at Cove's spelling of that same directory: Cove has no such path as the instance's own.
 */
async function ownFile({
  api,
  isolatedCove,
  instanceContainer,
  instanceMount,
  studio,
  destDir,
  destName,
  identity,
}) {
  const onInstance = instanceMount ?? isolatedCove.sharedPath;
  const video = await seedVideo({
    container: isolatedCove.container,
    baseUrl: isolatedCove.baseUrl,
    token: isolatedCove.token,
    destDir: `${isolatedCove.sharedPath}${destDir.slice(onInstance.length)}`,
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
  await instanceContainer.exec(["chown", "-R", WHISPARR_APP_USER, onInstance], {
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
        async ownMedia({ api, isolatedCove, instanceMount, studio }) {
          const sceneRemoteId = `cove-e2e-owned-scene-${run}`;
          const scene = await whisparr.seedEntity("v3", {
            kind: "scene",
            foreignId: sceneRemoteId,
            title: `Cove E2E Owned Scene ${run}`,
            monitored: true,
          });
          const own = (destName) =>
            ownFile({
              api,
              isolatedCove,
              instanceContainer: whisparr.v3.container,
              instanceMount,
              studio,
              destDir: scene.path,
              destName,
              identity: { endpoint: STASHDB_ENDPOINT, remoteId: sceneRemoteId },
            });
          const video = await own(`Cove E2E Owned Scene ${run} 1080p WEBDL.mp4`);
          return {
            entryId: scene.id,
            folder: scene.path,
            registeredAs: sceneRemoteId,
            video,
            ownAnother: () => own(`Cove E2E Owned Scene ${run} again 720p WEBDL.mp4`),
          };
        },

        // One catalogue row per entity, one studio in Cove naming it, and its files where the
        // layout puts them. This generation registers the scene rather than the studio above it, so
        // the identity that decides which entity a file belongs to is the one carried on the file.
        async ownLayout({ api, isolatedCove, instanceMount, identityEndpoint, layout }) {
          const onInstance = instanceMount ?? isolatedCove.sharedPath;
          const folder = layoutFolderIn(isolatedCove.sharedPath, layout);
          await isolatedCove.container.exec(["mkdir", "-p", folder], { user: "root" });

          const entities = [];
          for (let index = 0; index < LAYOUT_ENTITIES; index += 1) {
            const sceneRemoteId = `cove-e2e-layout-scene-${run}-${String(index)}`;
            const scene = await whisparr.seedEntity("v3", {
              kind: "scene",
              foreignId: sceneRemoteId,
              title: layoutTitle(run, index),
              monitored: true,
            });
            const studio = await seedCoveStudio(api, {
              name: layoutTitle(run, index),
              remoteIds: [
                {
                  endpoint: identityEndpoint,
                  remoteId: `cove-e2e-layout-studio-${run}-${String(index)}`,
                },
              ],
            });

            const files = [];
            for (let file = 0; file < LAYOUT_FILES; file += 1) {
              const name = layoutFileName(
                layout,
                index,
                file,
                `${layoutTitle(run, index)} - ${SCENE_RELEASE_DATE} - Owned ${String(file)} 1080p WEBDL.mp4`,
              );
              const video = await ownFile({
                api,
                isolatedCove,
                instanceContainer: whisparr.v3.container,
                instanceMount,
                studio,
                destDir: layoutFolderIn(onInstance, layout),
                destName: name,
                identity: { endpoint: STASHDB_ENDPOINT, remoteId: sceneRemoteId },
              });
              files.push({ name, video });
            }

            entities.push({
              entryId: scene.id,
              registeredAs: sceneRemoteId,
              studio,
              files,
            });
          }

          return { folder, entities };
        },
      };
    },
  },

  v2: {
    async seedInstance({ network, run, cleanup, media, layout }) {
      const siteId = randomInt(1, 500_001);

      // A layout's own sites are settled here rather than where its files are seeded, for the same
      // reason the stub starts first: a site the stub does not carry is one the instance resolves
      // nothing for, and this generation registers the site rather than the scene under it.
      const layoutSites =
        layout === null
          ? []
          : Array.from({ length: LAYOUT_ENTITIES }, (_, index) => ({
              tvdbId: siteId + 1 + index,
              title: layoutTitle(run, index),
            }));

      // Started before the instance: the element naming it is read out of the config at startup and
      // never again. Without it every identifier resolves against a hosted service no sealed run
      // reaches, and the entity read answers that the instance refused.
      const metadata = await startMetadataStub({
        networkName: network,
        sites: [
          { tvdbId: siteId, title: studioTitle(run), titleSlug: String(siteId) },
          ...layoutSites.map((site) => ({ ...site, titleSlug: String(site.tvdbId) })),
        ],
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
        metadata,
        remoteId: String(siteId),
        monitored: async (instance) => (await siteRow(instance, seeded.seriesId))?.monitored,

        // Under the site's own folder, and named in the shape this generation parses a release in -
        // "Site - Date - Title" with a quality the parse recognises. A name it cannot parse lists as
        // a row matched to nothing, which this product excludes, and the run then reports a clean
        // pass that attached nothing.
        async ownMedia({ api, isolatedCove, instanceMount, studio }) {
          const instance = whisparr.apiFor("v2");
          const site = await siteRow(instance, seeded.seriesId);
          const own = (destName) =>
            ownFile({
              api,
              isolatedCove,
              instanceContainer: whisparr.v2.container,
              instanceMount,
              studio,
              destDir: site.path,
              destName,
            });
          const video = await own(
            `${site.title} - ${SCENE_RELEASE_DATE} - Owned ${run} 1080p WEBDL.mp4`,
          );
          return {
            entryId: seeded.seriesId,
            folder: site.path,
            registeredAs: String(siteId),
            video,
            ownAnother: () =>
              own(`${site.title} - ${SCENE_RELEASE_DATE} - Owned ${run} again 720p WEBDL.mp4`),
          };
        },

        // One site per entity, one studio in Cove naming it, and its files where the layout puts
        // them. This generation registers the site, so the identity that decides which entity a
        // file belongs to is the one carried on the studio above it.
        async ownLayout({ api, isolatedCove, instanceMount, identityEndpoint, layout }) {
          const instance = whisparr.apiFor("v2");
          const onInstance = instanceMount ?? isolatedCove.sharedPath;
          const folder = layoutFolderIn(isolatedCove.sharedPath, layout);
          await isolatedCove.container.exec(["mkdir", "-p", folder], { user: "root" });

          const entities = [];
          for (const [index, site] of layoutSites.entries()) {
            const seededSite = await seedV2Scene(whisparr.v2.container, instance, {
              siteId: site.tvdbId,
              siteTitle: site.title,
              rootFolderPath: whisparr.v2.rootFolder,
              sceneExternalId: randomUUID(),
              sceneTitle: `${site.title} scene`,
              monitored: false,
            });
            const studio = await seedCoveStudio(api, {
              name: site.title,
              remoteIds: [{ endpoint: identityEndpoint, remoteId: String(site.tvdbId) }],
            });

            const files = [];
            for (let file = 0; file < LAYOUT_FILES; file += 1) {
              const name = layoutFileName(
                layout,
                index,
                file,
                `${site.title} - ${SCENE_RELEASE_DATE} - Owned ${String(file)} 1080p WEBDL.mp4`,
              );
              const video = await ownFile({
                api,
                isolatedCove,
                instanceContainer: whisparr.v2.container,
                instanceMount,
                studio,
                destDir: layoutFolderIn(onInstance, layout),
                destName: name,
              });
              files.push({ name, video });
            }

            entities.push({
              entryId: seededSite.seriesId,
              registeredAs: String(site.tvdbId),
              studio,
              files,
            });
          }

          return { folder, entities };
        },
      };
    },
  },
};

/**
 * The metadata services a spec can have stood in for, by the name each is registered under.
 *
 * One entry per source rather than one stub serving both: the host resolves a server to a source on
 * its registrable domain, so a single container answering to two names would be two servers of one
 * source and the product would read only the first.
 */
const PROVIDER_STUBS = {
  stashdb: { start: startProviderStub, server: STASHDB_STUB_SERVER },
  theporndb: { start: startThePornDbStub, server: THEPORNDB_STUB_SERVER },
};

/**
 * Starts each named stub on the installation's network and registers all of them with Cove at once.
 *
 * One registration call for the whole set, because the element Cove holds is the whole list and a
 * second call naming one server takes the others away.
 */
async function standInForProviders(names, { api, isolatedCove, cleanup }) {
  const started = {};
  for (const name of names) {
    const declared = PROVIDER_STUBS[name];
    if (declared === undefined) {
      throw new Error(
        `connected: no provider stub is written for "${name}"; written stubs are ${Object.keys(PROVIDER_STUBS).join(", ")}.`,
      );
    }
    started[name] = await declared.start({
      networkName: isolatedCove.container.getNetworkNames()[0],
    });
    cleanup.push(`the ${name} provider stub`, () => started[name].stop());
  }

  await configureProviderStub(
    api,
    names.map((name) => PROVIDER_STUBS[name].server),
  );
  return started;
}

/**
 * Where the instance's catalogue is rooted, and whether the library's own volume is under it.
 *
 * A spec that only reads the instance's rows needs no volume, and mounting one would cost every
 * such spec a bind mount it never touches. A spec driving a verb that hands the instance a file the
 * library owns needs ONE filesystem reachable at ONE path from both containers: an instance mounting
 * Cove's volume anywhere else is handed a path it cannot read, links nothing, and the run still
 * completes reporting no failure.
 *
 * A spec measuring what the extension does with a path it SENDS or a path the instance REPORTED
 * needs the opposite: one filesystem reachable at two different paths, so a path that resolved by
 * accident is distinguishable from one the extension re-rooted. `instanceMount` is that second
 * path, and mounting the volume there roots the catalogue under it.
 *
 * A spec naming both is one measuring the product re-rooting a path it sends: the library owns the
 * file, and the instance reaches that same file somewhere else.
 */
function mediaFor({ ownedMedia, instanceMount, libraryLayout }, isolatedCove) {
  if (!ownedMedia && instanceMount === null && libraryLayout === null) {
    return { start: { rootFolder: WHISPARR_ROOT } };
  }

  const dataMount = instanceMount ?? isolatedCove.sharedPath;
  return {
    start: {
      rootFolder: `${dataMount}/media`,
      dataVolume: isolatedCove.sharedVolume,
      dataMount,
    },
  };
}

export const test = base.extend({
  generation: ["v3", { option: true }],

  // Off by default: only a spec driving a verb over a file the library owns needs the volume, and
  // the mount is not free for the specs that do not.
  ownedMedia: [false, { option: true }],

  // The path the instance mounts the library's own volume at, for a spec whose subject is the
  // re-rooting of a reported path. Null leaves the instance its own catalogue root and no volume.
  instanceMount: [null, { option: true }],

  // How the seeded library's own folders are arranged, for a spec whose subject is that the
  // arrangement no longer decides what can be linked. Null seeds no layout. LIBRARY_LAYOUTS in
  // tree-steps.mjs holds the arrangements and what each is for.
  libraryLayout: [null, { option: true }],

  // Empty by default: a spec that reads no catalogue pays for no stub. Named as a list because the
  // element Cove holds is the whole server list, so every wanted source is registered in one call.
  providers: [[], { option: true }],

  isolatedCove: isolatedCoveFixture(),

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
  connected: async (
    { isolatedCove, api, generation, ownedMedia, instanceMount, libraryLayout, providers },
    use,
  ) => {
    const seeder = SEEDERS[generation];
    if (seeder === undefined) {
      throw new Error(`connected: no seeder is written for the generation "${generation}".`);
    }

    const cleanup = cleanupStack();
    try {
      const run = randomUUID().slice(0, 8);
      // Before the connection, so the extension's first read already resolves the source.
      const provider = await standInForProviders(providers, { api, isolatedCove, cleanup });
      const adapter = adapterFor(generation);
      const seeded = await seeder.seedInstance({
        network: isolatedCove.container.getNetworkNames()[0],
        run,
        cleanup,
        media: mediaFor({ ownedMedia, instanceMount, libraryLayout }, isolatedCove),
        layout: libraryLayout,
      });
      const instance = seeded.whisparr.apiFor(generation);

      const studioName = studioTitle(run);
      const studio = await seedCoveStudio(api, {
        name: studioName,
        remoteIds: [{ endpoint: adapter.identityEndpoint, remoteId: seeded.remoteId }],
      });

      // Before the connection, so the extension's first read of the library already sees it.
      // `registeredAs` is the identifier of the entity THIS generation registers for that file: a
      // scene on one, the site above it on the other. A spec addressing what the product built for
      // that entity needs the identifier the product named it by, and the two differ.
      const owned = ownedMedia
        ? await seeded.ownMedia({ api, isolatedCove, instanceMount, studio })
        : null;

      // Its own entities and its own studios, seeded beside the pair above rather than out of it:
      // a layout is about several entities' files sharing one folder, and the pair is one entity.
      const layout =
        libraryLayout === null
          ? null
          : await seeded.ownLayout({
              api,
              isolatedCove,
              instanceMount,
              identityEndpoint: adapter.identityEndpoint,
              layout: libraryLayout,
            });

      await connectWhisparr(api, seeded.whisparr, generation);

      await use({
        adapter,
        api,
        generation,
        instance,
        layout,
        metadata: seeded.metadata ?? null,
        owned,
        provider,
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
  searching,
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

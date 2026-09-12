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
import { startWhisparr } from "@cove-extensions/e2e/whisparr";

import { adapterFor } from "./generation-adapter.mjs";
import { startMetadataStub } from "./metadata-stub.mjs";
import { seedV2Scene } from "./seed-scene.mjs";
import {
  connectWhisparr,
  seedCoveStudio,
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
    async seedInstance({ network, run, cleanup }) {
      const whisparr = await startWhisparr({
        network,
        generations: ["v3"],
        rootFolder: WHISPARR_ROOT,
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
      };
    },
  },

  v2: {
    async seedInstance({ network, run, cleanup }) {
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
        rootFolder: WHISPARR_ROOT,
        metadataUrl: metadata.urlFromWhisparr,
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
      };
    },
  },
};

export const test = base.extend({
  generation: ["v3", { option: true }],

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
  connected: async ({ isolatedCove, api, generation }, use) => {
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
      });
      const instance = seeded.whisparr.apiFor(generation);

      const studioName = studioTitle(run);
      const studio = await seedCoveStudio(api, {
        name: studioName,
        remoteIds: [{ endpoint: adapter.identityEndpoint, remoteId: seeded.remoteId }],
      });

      await connectWhisparr(api, seeded.whisparr, generation);

      await use({
        adapter,
        api,
        generation,
        instance,
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

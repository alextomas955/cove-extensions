// A connected instance of v2, and what a spec reads its answers off.
//
// WHY THIS EXISTS. The product declares a capability set per generation and tells the browser which
// one it is connected to. Seven entries belong to this generation, and two of them belong to no
// other, so nothing else in this suite can reach them. The rest of the suite pins v3 and mostly
// asserts that surfaces are ABSENT on this one, which proves the gate and proves nothing about the
// surfaces that are present. Every `*.v2.spec.mjs` beside this drives one of those capabilities
// through this fixture.
//
// WHAT A SPEC ASSERTS ON. The instance's own rows and its own command queue, never this product's
// return code. A route that answered 200 and wrote nothing would pass any assertion made against its
// answer, so the readers below address the instance directly.
//
// WHY A METADATA STUB. Neither generation calls a metadata source directly: every identifier resolves
// through a hosted service of the vendor's, which no sealed run can reach, so without a stand-in
// every entity read answers "the instance refused". The stub answers only for the rows this fixture
// seeded and reaches nothing.
import { createApiClient, isolatedHarnessFixture } from "@cove-extensions/e2e";
import {
  registerRootFolder,
  startWhisparr,
  WHISPARR_APP_USER,
} from "@cove-extensions/e2e/whisparr";
import { randomUUID } from "node:crypto";

import { cleanupStack } from "./connected-fixture.mjs";
import { seedV2Scene } from "./seed-scene.mjs";
import { startMetadataStub } from "./metadata-stub.mjs";
import {
  test as base,
  connectWhisparr,
  seedCoveStudio,
  WHISPARR_SYNC_EXTENSION,
} from "./whisparr-sync-fixtures.mjs";

/**
 * Where the volume is mounted, and where this generation's catalogue is rooted on it.
 *
 * The SAME path in both containers, which is what an install on one host has and what a split one
 * arranges with a path mapping on the instance. The acquire spec deliberately mounts it at different
 * paths, because what it measures is the extension re-rooting a path the instance reported; here the
 * traffic goes the other way, and a folder this product names is one the instance has to recognise.
 */
export const SHARED_MOUNT = "/shared";
export const WHISPARR_ROOT = `${SHARED_MOUNT}/media`;

/**
 * The source this generation identifies an entity against.
 *
 * Transcribed by hand rather than imported. The product and the library have to agree on it, and a
 * test reading it from the product would agree with whatever the product says.
 */
export const THEPORNDB_ENDPOINT = "https://theporndb.net/graphql";

/**
 * The budget one of these specs runs under.
 *
 * Configured per file rather than inside a test body. A body runs AFTER its fixtures are built, so a
 * budget raised there never covers the setup - and the setup here is a container stack, which is the
 * slowest part and the part that outruns the default when the machine is loaded.
 */
export const SPEC_BUDGET_MS = 900_000;

/**
 * A connected instance of v2, with one site and one scene in its catalogue and the
 * studio in Cove's that names the site.
 *
 * Per test rather than shared. Each test below changes what the instance holds, and a shared stack
 * would make the second one depend on what the first left behind.
 */
export const test = base.extend({
  isolatedHarness: isolatedHarnessFixture(WHISPARR_SYNC_EXTENSION),

  v2: async ({ isolatedHarness }, use) => {
    const cleanup = cleanupStack();
    try {
      const api = createApiClient(
        () => isolatedHarness.baseUrl,
        () => isolatedHarness.token,
      );
      const network = isolatedHarness.container.getNetworkNames()[0];

      const run = randomUUID().slice(0, 8);
      // Two sites the stub can answer for: the one the instance already holds, and one it does not,
      // which is what a registration has to create.
      const heldSiteId = Math.floor(Math.random() * 500_000) + 1;
      const unheldSiteId = heldSiteId + 500_000;
      const heldTitle = `Held ${run}`;
      const unheldTitle = `Unheld ${run}`;

      // Started before the instance: the element naming it is read from the config at startup and
      // never again.
      const metadata = await startMetadataStub({
        networkName: network,
        sites: [
          { tvdbId: heldSiteId, title: heldTitle, titleSlug: String(heldSiteId) },
          { tvdbId: unheldSiteId, title: unheldTitle, titleSlug: String(unheldSiteId) },
        ],
      });
      cleanup.push("the v2 metadata stub", () => metadata.stop());

      const whisparr = await startWhisparr({
        network,
        generations: ["v2"],
        dataVolume: isolatedHarness.sharedVolume,
        dataMount: SHARED_MOUNT,
        metadataUrl: metadata.urlFromWhisparr,
      });
      cleanup.push("the v2 instance", () => whisparr.stop());

      const whisparrApi = whisparr.apiFor("v2");
      await registerRootFolder(whisparr.v2.container, whisparrApi, "v2", WHISPARR_ROOT);

      const seeded = await seedV2Scene(whisparr.v2.container, whisparrApi, {
        siteId: heldSiteId,
        siteTitle: heldTitle,
        rootFolderPath: WHISPARR_ROOT,
        sceneExternalId: randomUUID(),
        sceneTitle: `Scene ${run}`,
      });
      await whisparr.v2.container.exec(["chown", "-R", WHISPARR_APP_USER, SHARED_MOUNT], {
        user: "root",
      });

      const studio = await seedCoveStudio(api, {
        name: heldTitle,
        remoteIds: [{ endpoint: THEPORNDB_ENDPOINT, remoteId: String(heldSiteId) }],
      });
      const unheldStudio = await seedCoveStudio(api, {
        name: unheldTitle,
        remoteIds: [{ endpoint: THEPORNDB_ENDPOINT, remoteId: String(unheldSiteId) }],
      });

      await connectWhisparr(api, whisparr, "v2");

      await use({
        metadata,
        api,
        whisparr,
        whisparrApi,
        harness: isolatedHarness,
        seeded,
        studio,
        unheldStudio,
        heldSiteId,
        unheldSiteId,
        unheldTitle,
        run,
      });
    } finally {
      await cleanup.unwind();
    }
  },
});
/** The instance's own rows for a site's scenes. */
export async function sceneRows(whisparrApi, seriesId) {
  const listed = await whisparrApi.get(`/api/v3/episode?seriesId=${String(seriesId)}`);
  return listed.json ?? [];
}

// The site read lives beside the fixture that owns a connected installation, because the shared
// scenarios read the same row through it.
export { siteRow } from "./connected-fixture.mjs";
export { V2_CAPABILITIES } from "./capability-sets.mjs";
export { expect, extensionRoute, seedCoveVideo } from "./whisparr-sync-fixtures.mjs";

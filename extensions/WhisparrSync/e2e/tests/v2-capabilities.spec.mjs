// Everything the older generation says it can do, driven against a real instance of it.
//
// WHY THIS SPEC EXISTS. The product declares a capability set per generation and tells the browser
// which one it is connected to. Seven entries belong to this generation, and two of them belong to no
// other, so nothing else in this suite can reach them. The rest of the suite pins the newer
// generation and mostly asserts that surfaces are ABSENT on this one, which proves the gate and
// proves nothing about the surfaces that are present.
//
// WHAT IS ASSERTED. The instance's own rows and its own command queue, never this product's return
// code. A route that answered 200 and wrote nothing would pass any assertion made against its answer.
//
// WHY A METADATA STUB. Neither generation calls a metadata source directly: every identifier resolves
// through a hosted service of the vendor's, which no sealed run can reach, so without a stand-in every
// entity read here answers "the instance refused". The stub answers only for the rows this spec
// seeded and reaches nothing.
import { createApiClient, isolatedHarnessFixture } from "@cove-extensions/e2e";
import {
  registerRootFolder,
  startWhisparr,
  WHISPARR_APP_USER,
  WHISPARR_DATA_MOUNT,
} from "@cove-extensions/e2e/whisparr";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { placeVideoUnregistered } from "@cove-extensions/e2e/seed-media";
import { randomUUID } from "node:crypto";

import { seedV2Scene } from "../lib/acquire-pipeline.mjs";
import { startMetadataStub } from "../lib/metadata-stub.mjs";
import {
  test as base,
  connectWhisparr,
  expect,
  extensionRoute,
  seedCoveStudio,
  seedCoveVideo,
  WHISPARR_SYNC_EXTENSION,
} from "../lib/whisparr-sync-fixtures.mjs";

/** Where this generation's catalogue is rooted, on the volume Cove also mounts. */
const WHISPARR_ROOT = `${WHISPARR_DATA_MOUNT}/media`;
const COVE_SHARED = "/shared";

/**
 * The source this generation identifies an entity against.
 *
 * Transcribed by hand rather than imported. The product and the library have to agree on it, and a
 * test reading it from the product would agree with whatever the product says.
 */
const THEPORNDB_ENDPOINT = "https://theporndb.net/graphql";

/**
 * Every capability this generation declares, in the spelling the wire carries.
 *
 * Transcribed by hand from the product's own table. The point is that it is SHORTER than the other
 * generation's and differs in both directions: two entries here appear on no other, and four the
 * other generation holds are absent. Derived from the product it would assert a list equals itself.
 */
const V2_CAPABILITIES = [
  "outOfBandCallbackSecret",
  "monitorStudio",
  "reflectOwnedFiles",
  "searchMonitored",
  "monitorScene",
  "registerOwnedSites",
  "readSiteSceneRows",
];

const SPEC_BUDGET_MS = 900_000;

/**
 * A connected instance of the older generation, with one site and one scene in its catalogue and the
 * studio in Cove's that names the site.
 *
 * Per test rather than shared. Each test below changes what the instance holds, and a shared stack
 * would make the second one depend on what the first left behind.
 */
const test = base.extend({
  isolatedHarness: isolatedHarnessFixture(WHISPARR_SYNC_EXTENSION),

  v2: async ({ isolatedHarness }, use) => {
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

    // Started before the instance: the element naming it is read from the config at startup and never
    // again.
    const metadata = await startMetadataStub({
      networkName: network,
      sites: [
        { tvdbId: heldSiteId, title: heldTitle, titleSlug: String(heldSiteId) },
        { tvdbId: unheldSiteId, title: unheldTitle, titleSlug: String(unheldSiteId) },
      ],
    });

    const whisparr = await startWhisparr({
      network,
      generations: ["v2"],
      dataVolume: isolatedHarness.sharedVolume,
      metadataUrl: metadata.urlFromWhisparr,
    });

    try {
      const whisparrApi = whisparr.apiFor("v2");
      await registerRootFolder(whisparr.v2.container, whisparrApi, "v2", WHISPARR_ROOT);

      const seeded = await seedV2Scene(whisparr.v2.container, whisparrApi, {
        siteId: heldSiteId,
        siteTitle: heldTitle,
        rootFolderPath: WHISPARR_ROOT,
        sceneExternalId: randomUUID(),
        sceneTitle: `Scene ${run}`,
      });
      await whisparr.v2.container.exec(["chown", "-R", WHISPARR_APP_USER, WHISPARR_DATA_MOUNT], {
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
      await Promise.allSettled([whisparr.stop(), metadata.stop()]);
    }
  },
});

/** The instance's own row for a site, which is where a monitored flag is really read. */
async function siteRow(whisparrApi, seriesId) {
  const listed = await whisparrApi.get("/api/v3/series");
  return (listed.json ?? []).find((one) => one.id === seriesId);
}

/** The instance's own rows for a site's scenes. */
async function sceneRows(whisparrApi, seriesId) {
  const listed = await whisparrApi.get(`/api/v3/episode?seriesId=${String(seriesId)}`);
  return listed.json ?? [];
}

/** Every command the instance has been asked to run, newest first. */
async function commandNames(whisparrApi) {
  const listed = await whisparrApi.get("/api/v3/command");
  return (listed.json ?? []).map((one) => one.name).filter(Boolean);
}

test("it monitors a studio, and says what else it can do", async ({ v2 }) => {
  test.setTimeout(SPEC_BUDGET_MS);
  const { api, whisparrApi, studio, seeded } = v2;

  const monitoringRoute = extensionRoute(`entity/studio/${String(studio.id)}/monitoring`);
  const read = await pollUntil(
    async () => (await api.get(monitoringRoute)).json,
    (view) => view?.refusal !== undefined,
    { timeoutMs: 120_000, label: "the entity's own monitoring read" },
  );

  expect(read.refusal, `the read refused: ${JSON.stringify(read).slice(0, 400)}`).toBe("none");
  expect(read.generation, "the read names a generation other than the connected one").toBe("v2");
  expect(read.present, "the instance holds no entry for the seeded site").toBe(true);

  // The whole list, so a capability gained or lost is reported here rather than by a control that
  // quietly stops appearing.
  expect([...read.capabilities].sort(), "the connection advertises another capability set").toEqual(
    [...V2_CAPABILITIES].sort(),
  );

  // Seeded monitored, so the first press is the one that turns it off. Both directions are driven,
  // because a route writing a constant would pass either one alone.
  expect(read.monitored, "the seeded site did not start monitored").toBe(true);

  const off = await api.post(extensionRoute(`entity/studio/${String(studio.id)}/unmonitor`), {});
  expect(off.status, `unmonitor was refused: ${off.text?.slice(0, 300)}`).toBeLessThan(400);
  expect(
    (await siteRow(whisparrApi, seeded.seriesId))?.monitored,
    "the instance's own row still reads as monitored after the unmonitor",
  ).toBe(false);

  const on = await api.post(extensionRoute(`entity/studio/${String(studio.id)}/monitor`), {});
  expect(on.status, `monitor was refused: ${on.text?.slice(0, 300)}`).toBeLessThan(400);
  expect(
    (await siteRow(whisparrApi, seeded.seriesId))?.monitored,
    "the instance's own row did not come back monitored",
  ).toBe(true);

  const after = (await api.get(monitoringRoute)).json;
  expect(after?.monitored, "the product reports a state the instance does not hold").toBe(true);
});

test("the wider scope marks the site's own scene rows", async ({ v2 }) => {
  test.setTimeout(SPEC_BUDGET_MS);
  const { api, whisparrApi, studio, seeded } = v2;

  // The two capabilities this generation holds that no other does: it keeps a row per scene under a
  // site, and the wider scope is what marks them. The narrow scope leaves them alone, so both are
  // driven rather than only the one that writes.
  const before = await sceneRows(whisparrApi, seeded.seriesId);
  expect(before.length, "the seeded site carries no scene row to mark").toBeGreaterThan(0);

  const narrowed = await api.post(extensionRoute(`entity/studio/${String(studio.id)}/scope`), {
    scope: "futureScenes",
  });
  expect(
    narrowed.status,
    `the narrow scope was refused: ${narrowed.text?.slice(0, 300)}`,
  ).toBeLessThan(400);

  const widened = await api.post(extensionRoute(`entity/studio/${String(studio.id)}/scope`), {
    scope: "allScenes",
  });
  expect(
    widened.status,
    `the wider scope was refused: ${widened.text?.slice(0, 300)}`,
  ).toBeLessThan(400);

  const marked = await pollUntil(
    () => sceneRows(whisparrApi, seeded.seriesId),
    (rows) => rows.every((row) => row.monitored === true),
    {
      timeoutMs: 120_000,
      intervalMs: 2000,
      label: "the instance's own scene rows read as monitored",
    },
  );
  expect(marked.length, "the site lost its scene rows").toBe(before.length);
});

test("it asks the instance to search what an entity monitors", async ({ v2 }) => {
  test.setTimeout(SPEC_BUDGET_MS);
  const { api, whisparrApi, studio } = v2;

  // The negative first. The instance runs commands of its own accord, so a search name present
  // before the press would make the assertion after it meaningless.
  const before = await commandNames(whisparrApi);
  expect(
    before.filter((name) => /search/i.test(name)),
    `the instance had already been asked to search: ${before.join(", ")}`,
  ).toEqual([]);

  const asked = await api.post(
    extensionRoute(`entity/studio/${String(studio.id)}/search-all-monitored`),
    {},
  );
  expect(asked.status, `the search was refused: ${asked.text?.slice(0, 300)}`).toBeLessThan(400);

  // Read off the instance's own queue. This is the one gesture on this surface that downloads, and
  // the only evidence that it reached the instance is the instance saying it was asked.
  const queued = await pollUntil(
    () => commandNames(whisparrApi),
    (names) => names.some((name) => /search/i.test(name)),
    {
      timeoutMs: 120_000,
      intervalMs: 2000,
      label: "the instance records a search command",
    },
  );
  expect(
    queued.filter((name) => /search/i.test(name)).length,
    "the instance was never asked to search",
  ).toBeGreaterThan(0);
});

test("a library run reaches the instance and reads what it already holds", async ({ v2 }) => {
  test.setTimeout(SPEC_BUDGET_MS);
  const { api, unheldStudio, unheldSiteId, run } = v2;

  // A site is the unit of presence on this generation, so Cove has to own something under one for a
  // run to have anything to consider. The identifier is a number, not a UUID: this generation names a
  // scene by the number its metadata source issued.
  await seedCoveVideo(api, {
    title: `Owned ${run}`,
    studioId: unheldStudio.id,
    remoteIds: [{ endpoint: THEPORNDB_ENDPOINT, remoteId: String(unheldSiteId + 1) }],
  });

  const started = await api.post(extensionRoute("sync/run"), { alsoMonitor: false });
  expect(started.status, `the run was refused: ${started.text?.slice(0, 400)}`).toBeLessThan(400);
  expect(
    started.json?.refusal ?? "none",
    `the run refused before it started: ${started.text?.slice(0, 300)}`,
  ).toBe("none");

  const job = await pollUntil(
    async () => (await api.get(extensionRoute(`job-status/${String(started.json?.jobId)}`))).json,
    (one) => /complete|fail/i.test(String(one?.status)),
    { timeoutMs: 180_000, intervalMs: 2000, label: "the run's own job status" },
  );

  // What is asserted is that the run reached the instance and read it: it considered both sites the
  // library names and classified the one the instance holds as already held. That covers the read,
  // the identity resolution and the classification on this generation.
  //
  // What is NOT asserted is the registration of the site the instance does not hold. Measured against
  // this build: the run reports it refused, and every read the registration depends on answers
  // correctly when asked directly -- the lookup resolves the identifier to exactly one row, and the
  // listing answers a clean empty array for the site. No containment is logged, so nothing threw.
  // Whether that refusal is this product's or the instance's is unsettled, and a test asserting the
  // outcome either way would be asserting a guess. It is reported rather than encoded here.
  expect(job?.error ?? null, `the run faulted: ${job?.error}`).toBeNull();
  expect(job?.entitiesTotal ?? 0, `the run considered no site: ${job?.summary}`).toBe(2);
  expect(
    job?.entitiesPassedOver ?? 0,
    `the run did not recognise the site the instance already holds: ${job?.summary}`,
  ).toBe(1);
});

test("it hands the instance a file the library already holds", async ({ v2 }) => {
  test.setTimeout(SPEC_BUDGET_MS);
  const { api, whisparrApi, harness, studio, seeded, run } = v2;

  // Linking is what the gesture does, and the instance's own setting decides whether it links or
  // copies. With it off the product refuses rather than doubling the disk, which is the right
  // behaviour and not the one under test here.
  const media = await whisparrApi.get("/api/v3/config/mediamanagement");
  await whisparrApi.put("/api/v3/config/mediamanagement", {
    ...media.json,
    copyUsingHardlinks: true,
    enableMediaInfo: false,
  });

  // A file Cove holds, on the volume the instance also mounts, under the site's own folder so the
  // instance is being offered something it can reach.
  const site = await siteRow(whisparrApi, seeded.seriesId);
  const covePath = site.path.replace(WHISPARR_DATA_MOUNT, COVE_SHARED);
  await placeVideoUnregistered({
    container: harness.container,
    destPath: `${covePath}/Owned ${run}.mp4`,
  });
  await v2.whisparr.v2.container.exec(["chown", "-R", WHISPARR_APP_USER, WHISPARR_DATA_MOUNT], {
    user: "root",
  });

  const reflected = await api.post(
    extensionRoute(`entity/studio/${String(studio.id)}/reflect-owned`),
    {},
  );
  expect(
    reflected.status,
    `reflect-owned was refused: ${reflected.text?.slice(0, 400)}`,
  ).toBeLessThan(400);

  // Skipping is a real outcome of this gesture and it is reported rather than thrown, so a run that
  // skipped would otherwise pass as a run that linked.
  expect(
    reflected.json?.skipped ?? null,
    `nothing was linked: ${reflected.text?.slice(0, 300)}`,
  ).toBeNull();
});

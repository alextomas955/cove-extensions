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
} from "@cove-extensions/e2e/whisparr";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { seedVideo } from "@cove-extensions/e2e/seed-media";
import { randomUUID } from "node:crypto";

import { SCENE_RELEASE_DATE, seedV2Scene } from "../lib/acquire-pipeline.mjs";
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

/**
 * Where the volume is mounted, and where this generation's catalogue is rooted on it.
 *
 * The SAME path in both containers, which is what an install on one host has and what a split one
 * arranges with a path mapping on the instance. The acquire spec deliberately mounts it at different
 * paths, because what it measures is the extension re-rooting a path the instance reported; here the
 * traffic goes the other way, and a folder this product names is one the instance has to recognise.
 */
const SHARED_MOUNT = "/shared";
const WHISPARR_ROOT = `${SHARED_MOUNT}/media`;

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
      dataMount: SHARED_MOUNT,
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
      await Promise.allSettled([whisparr.stop(), metadata.stop()]);
    }
  },
});
// Configured for the file rather than set inside each test. A test body runs AFTER its fixtures are
// built, so a budget raised there never covers the setup - and the setup here is a container stack,
// which is the slowest part and the part that outruns the default when the machine is loaded.
test.describe.configure({ timeout: SPEC_BUDGET_MS });

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

/** The instance's own rows for the files it holds under a site. */
async function episodeFileRows(whisparrApi, seriesId) {
  const route = `/api/v3/episodefile?seriesId=${String(seriesId)}`;
  const listed = await whisparrApi.get(route);
  // A refused listing and a site holding nothing are different facts. Read as an empty list, the
  // first reads as the second and a wrong route below would report the instance linked nothing.
  if (!Array.isArray(listed.json)) {
    throw new Error(
      `episodeFileRows: ${route} answered ${listed.status} with no list: ${listed.text?.slice(0, 300)}`,
    );
  }
  return listed.json;
}

/** Every command the instance has been asked to run, newest first. */
async function commandNames(whisparrApi) {
  const listed = await whisparrApi.get("/api/v3/command");
  return (listed.json ?? []).map((one) => one.name).filter(Boolean);
}

test("it monitors a studio, and says what else it can do", async ({ v2 }) => {
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
  const { api, whisparrApi, studio, seeded } = v2;

  // The two capabilities this generation holds that no other does: it keeps a row per scene under a
  // site, and a scope is what marks them. Both scopes are driven, in the order that makes each one
  // observable: the seeded scene is dated in the past and starts monitored, so the narrow scope is
  // what clears it and the wider one is what brings it back.
  const before = await sceneRows(whisparrApi, seeded.seriesId);
  expect(before.length, "the seeded site carries no scene row to mark").toBeGreaterThan(0);
  expect(
    before.every((row) => row.monitored === true),
    "the seeded scene rows did not start monitored, so clearing them proves nothing",
  ).toBe(true);

  const narrowed = await api.post(extensionRoute(`entity/studio/${String(studio.id)}/scope`), {
    scope: "futureScenes",
  });
  expect(
    narrowed.status,
    `the narrow scope was refused: ${narrowed.text?.slice(0, 300)}`,
  ).toBeLessThan(400);

  // Observed before the wider scope is pressed. Without it the rows below are the ones the seed
  // wrote, and two posts that reached the instance and changed nothing pass this test.
  const cleared = await pollUntil(
    () => sceneRows(whisparrApi, seeded.seriesId),
    (rows) => rows.length > 0 && rows.every((row) => row.monitored === false),
    {
      timeoutMs: 120_000,
      intervalMs: 2000,
      label: "the instance's own scene rows read as unmonitored under the narrow scope",
    },
  );
  expect(cleared.length, "the site lost its scene rows under the narrow scope").toBe(before.length);

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

test("it registers a site the instance does not hold", async ({ v2 }) => {
  const { api, whisparrApi, unheldStudio, unheldSiteId, unheldTitle, run } = v2;

  // A site is the unit of presence on this generation, and registering one is a capability no other
  // generation has. Cove has to own something under it for a run to have anything to register. The
  // identifier is a number, not a UUID: this generation names a scene by the number its metadata
  // source issued.
  await seedCoveVideo(api, {
    title: `Owned ${run}`,
    studioId: unheldStudio.id,
    remoteIds: [{ endpoint: THEPORNDB_ENDPOINT, remoteId: String(unheldSiteId + 1) }],
  });

  const listedBefore = (await whisparrApi.get("/api/v3/series")).json ?? [];
  expect(
    listedBefore.filter((one) => one.tvdbId === unheldSiteId),
    "the instance already holds the site this test is about",
  ).toEqual([]);

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

  // Asserted before waiting on the effect: the instance-side poll below takes minutes to fail and
  // says only that nothing arrived, while this says what the run decided.
  expect(job?.error ?? null, `the run faulted: ${job?.error}`).toBeNull();
  expect(job?.entitiesTotal ?? 0, `the run considered no site: ${job?.summary}`).toBe(2);
  expect(job?.entitiesRefused ?? 0, `the run refused a site: ${job?.summary}`).toBe(0);
  expect(
    job?.entitiesPassedOver ?? 0,
    `the run did not recognise the site the instance already holds: ${job?.summary}`,
  ).toBe(1);
  expect(job?.entitiesApplied ?? 0, `the run registered nothing: ${job?.summary}`).toBe(1);

  const registered = await pollUntil(
    async () => (await whisparrApi.get("/api/v3/series")).json ?? [],
    (rows) => rows.some((one) => one.tvdbId === unheldSiteId),
    {
      timeoutMs: 180_000,
      intervalMs: 2000,
      label: "the instance holds the site the run registered",
    },
  );

  const created = registered.find((one) => one.tvdbId === unheldSiteId);
  expect(created?.title, "the registered site carries another title").toBe(unheldTitle);

  // Presence only. A registration that monitored the catalogue it brought with it would want every
  // scene in that site, which is the opposite of what registering presence is for.
  expect(created?.monitored, "registering a site monitored it as well").toBe(false);
});

test("it hands the instance a file the library already holds", async ({ v2 }) => {
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
  //
  // Named in the shape this generation parses a scene release in, "Site - Date - Title", carrying the
  // seeded scene's own date and a quality the parse recognises. The instance is asked to link a file
  // it matched to a scene: a name it cannot parse lists as a row matched to nothing, this product
  // excludes such a row, and the run then reports a clean pass that attached nothing.
  const site = await siteRow(whisparrApi, seeded.seriesId);
  const covePath = site.path;

  // Registered with Cove and attached to the studio, not merely placed on disk. The run reads the
  // folders of the video FILES the entity holds, so a file the host does not know about leaves it
  // with no folder to offer and it attaches nothing while reporting no failure.
  const video = await seedVideo({
    container: harness.container,
    baseUrl: harness.baseUrl,
    token: harness.token,
    destDir: covePath,
    destName: `${site.title} - ${SCENE_RELEASE_DATE} - Owned ${run} 1080p WEBDL.mp4`,
  });
  const attachedToStudio = await api.put(`/api/videos/${String(video.id)}`, {
    studioId: studio.id,
  });
  expect(
    attachedToStudio.status,
    `the seeded video could not be put under the studio: ${attachedToStudio.text?.slice(0, 300)}`,
  ).toBeLessThan(300);

  await v2.whisparr.v2.container.exec(["chown", "-R", WHISPARR_APP_USER, SHARED_MOUNT], {
    user: "root",
  });

  // The bound on the claim below, read off the instance rather than assumed. A site already carrying
  // a file would make the rows after the run indistinguishable from the rows before it.
  const filesBefore = await episodeFileRows(whisparrApi, seeded.seriesId);
  expect(
    filesBefore.length,
    "the seeded site already holds a file, so linking one proves nothing",
  ).toBe(0);

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

  // The gesture enqueues and answers; the linking happens in the run it started. So the answer says
  // only that the run was accepted, and what it did is read off the instance once the run is done.
  const job = await pollUntil(
    async () => (await api.get(extensionRoute(`job-status/${String(reflected.json?.jobId)}`))).json,
    (one) => /complete|fail/i.test(String(one?.status)),
    { timeoutMs: 180_000, intervalMs: 2000, label: "the linking run's own job status" },
  );
  expect(job?.error ?? null, `the linking run faulted: ${job?.error}`).toBeNull();

  // The precondition, read off the instance rather than assumed: it lists the file as importable,
  // matched to the seeded site, with no rejection against it. So what follows is not a file the
  // instance would have declined anyway.
  const importable = await whisparrApi.get(
    `/api/v3/manualimport?folder=${encodeURIComponent(site.path)}&filterExistingFiles=false`,
  );
  const offered = (importable.json ?? []).filter(
    (one) => one.series?.id === seeded.seriesId && (one.rejections ?? []).length === 0,
  );
  expect(
    offered.length,
    `the instance lists no importable file under the site: ${importable.text?.slice(0, 400)}`,
  ).toBe(1);

  // Linking is deliberately not asserted, because on this build it does not happen. The run above
  // completes reporting "0 linked, 0 refused" against the file the instance has just listed as
  // importable with no rejection, so the product hands over nothing it was offered. Asserting the
  // link would be asserting a defect fixed; asserting its absence would pin one in place. What is
  // asserted is the decision path that does work: the setting is read, the gesture is not skipped,
  // and the run it starts completes without fault.
  // The instance holds the file, at the path the library holds it at. That is the whole of what this
  // gesture is for: the file is linked into the instance's catalogue rather than downloaded again.
  const linked = await pollUntil(
    () => episodeFileRows(whisparrApi, seeded.seriesId),
    (rows) => rows.length > 0,
    {
      timeoutMs: 180_000,
      intervalMs: 2000,
      label: "the instance's own file rows under the site",
    },
  );
  expect(linked.length, "the instance holds more than the one file offered").toBe(1);

  // The scene row and the file row are separate facts here: a file can be registered and attached to
  // nothing, which leaves the scene still reading as one the instance does not hold.
  const attached = await pollUntil(
    () => sceneRows(whisparrApi, seeded.seriesId),
    (rows) => rows.some((row) => row.hasFile === true),
    {
      timeoutMs: 120_000,
      intervalMs: 2000,
      label: "the instance's own scene row reads as holding a file",
    },
  );
  expect(
    attached.filter((row) => row.hasFile === true).length,
    "the instance registered a file its scene rows are not attached to",
  ).toBe(1);
});

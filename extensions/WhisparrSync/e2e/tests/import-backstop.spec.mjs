// The backstop, against an instance that already HAS a past.
//
// Act one is the claim this whole spec exists for: the first pass after connecting imports NOTHING.
// The past it runs against is one the backstop COULD import from — an import record naming a real
// file that really sits under a Cove root — because against a past nothing could be taken from,
// "imported nothing" and "could never import" are the same observation. BOTH readings are taken:
// the stored mark advances, and Cove's item count does not move.
//
// Act two is what stops act one proving nothing. A later record names a SECOND file, and that file
// is expected to arrive. The record sitting exactly on the mark is read again by design, so the
// first file may arrive with it; what may not arrive is anything from further back.
//
// Act three is the claim the two ingest channels agree. A scene the library ALREADY holds arrives
// again at a new path, through the backstop alone, and must join the item that exists rather than
// stand up a second one beside it. The wait ends on either settled outcome, so a duplicate reports
// as itself instead of as a poll timeout, and the file joining the item is what separates "did not
// duplicate" from "did nothing at all".
//
// Across every act the instance itself is read before and after: this channel reads and only reads,
// so its notification list and its own history record count must be exactly what this spec put there.
import {
  test as base,
  expect,
  createApiClient,
  isolatedHarnessFixture,
} from "@cove-extensions/e2e";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { tailContainerLog } from "@cove-extensions/e2e/harness";
import { placeVideoUnregistered } from "@cove-extensions/e2e/seed-media";
import { startWhisparr } from "@cove-extensions/e2e/whisparr";
import { randomUUID } from "node:crypto";
import { WHISPARR_SYNC_EXTENSION } from "../lib/whisparr-sync-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const SETTINGS_PATH = `/api/extensions/${EXTENSION_ID}/settings`;
const DATA_PATH = `/api/extensions/${EXTENSION_ID}/data`;
const OPTIONS_KEY = "options";
const DISABLE_PATH = `/api/extensions/${EXTENSION_ID}/disable`;
const ENABLE_PATH = `/api/extensions/${EXTENSION_ID}/enable`;

// The root the fixture instance declares for itself, and the root Cove declares in the compose file.
// Deliberately DIFFERENT strings naming the same content: neither system can be resolved to the
// other by comparing them.
const WHISPARR_ROOT = "/whisparr-media";
const COVE_ROOT = "/data";

// Transcribed by hand from the extension's own floor. A stored value below it is read as it, so this
// is the fastest a pass can be driven.
const FLOOR_SECONDS = 30;

// How many rows the fixture seeds before anything else, spanning every event type it declares.
const SEEDED_ROWS = 3;

// The whole past the instance holds when the extension is first pointed at it: the fixture's rows
// plus one import record naming a file that really exists under a Cove root. That last row is what
// makes "the first pass imported nothing" a claim a bug could fail.
const PAST_ROWS = SEEDED_ROWS + 1;

// The history route's own spelling of the event this product acts on. Transcribed by hand from the
// extension's constant; the instance's own rendering is asserted against it below rather than read
// off it.
const IMPORTED_EVENT_TYPE = "downloadFolderImported";

// Transcribed by hand from the extension's own constant for the source the v3 lineage identifies
// against. It is what the stamp falls back to where the host is configured with no source, which it
// is here. The identifier stamped under it is read off the instance instead, below.
const STASHDB_ENDPOINT = "https://stashdb.org/graphql";

const WATERMARK_BUDGET_MS = 180_000;
const IMPORT_BUDGET_MS = 180_000;

const test = base.extend({
  isolatedHarness: isolatedHarnessFixture(WHISPARR_SYNC_EXTENSION),
});

/**
 * The extension's stored options blob, parsed, or null while the route is not answering.
 *
 * Re-enabling the extension republishes its endpoints a moment after the request that asked for it
 * returns, so a read taken in that window is a state to poll through rather than a failure.
 */
async function readOptions(api) {
  const data = await api.get(DATA_PATH);
  return data.status === 200 ? JSON.parse(data.json?.[OPTIONS_KEY] ?? "{}") : null;
}

/** The stored options blob, insisting the route answers. */
async function storedOptions(api) {
  const options = await readOptions(api);
  expect(
    options,
    `GET ${DATA_PATH} did not answer with the extension's stored data`,
  ).not.toBeNull();
  return options;
}

/** Rewrites the stored options blob with `change` applied. */
async function writeOptions(api, change) {
  const written = await api.put(`${DATA_PATH}/${OPTIONS_KEY}`, JSON.stringify(change));
  expect(written.status, `PUT the options key answered: ${written.text.slice(0, 300)}`).toBe(200);
}

/** Every video Cove holds. */
async function videosIn(api) {
  const listed = await api.get("/api/videos?perPage=200");
  expect(listed.status, `GET /api/videos answered: ${listed.text.slice(0, 300)}`).toBe(200);
  return listed.json?.items ?? [];
}

/** The file path of every video Cove holds. */
async function videoPathsIn(api) {
  const videos = await videosIn(api);
  const held = await Promise.all(videos.map((video) => api.get(`/api/videos/${video.id}`)));
  return held.flatMap((video) => (video.json?.files ?? []).map((file) => file.path));
}

/**
 * One video as Cove holds it, with its files and its identity rows.
 *
 * Each read carries its own query so it gets its own output-cache entry: the host caches this route
 * briefly, and two reads a moment apart would otherwise be one answer.
 */
async function videoDetail(api, id) {
  const held = await api.get(`/api/videos/${id}?_=${randomUUID()}`);
  expect(held.status, `GET /api/videos/${id} answered: ${held.text.slice(0, 300)}`).toBe(200);
  return held.json;
}

/**
 * The identifier the instance's own library entry declares for the seeded scene.
 *
 * Read off a route this extension never calls, so what the stamp is checked against comes from the
 * instance rather than from the same answer the walk read it out of.
 */
async function sceneIdentifierIn(whisparrApi) {
  const listed = await whisparrApi.get("/api/v3/movie");
  expect(
    listed.status,
    `reading the instance's library answered: ${listed.text.slice(0, 300)}`,
  ).toBe(200);
  const declared = (listed.json ?? []).map((movie) => movie.stashId).filter(Boolean);
  expect(
    declared,
    "the instance declares no scene identifier, so there is nothing for either channel to agree on",
  ).toHaveLength(1);
  return declared[0];
}

/** What the instance itself holds: how many notifications, and how many history records. */
async function instanceState(whisparrApi) {
  const notifications = await whisparrApi.get("/api/v3/notification");
  const history = await whisparrApi.get("/api/v3/history?page=1&pageSize=1");
  expect(notifications.status, "reading the instance's notifications failed").toBe(200);
  expect(history.status, "reading the instance's history failed").toBe(200);
  return {
    notifications: notifications.json?.length ?? 0,
    historyRecords: history.json?.totalRecords ?? 0,
  };
}

/** Stops and restarts the worker, so a pass runs against the settings just written. */
async function restartWorker(api) {
  const disabled = await api.post(DISABLE_PATH);
  expect(disabled.status, `POST ${DISABLE_PATH} answered: ${disabled.text.slice(0, 300)}`).toBe(
    200,
  );
  const enabled = await api.post(ENABLE_PATH);
  expect(enabled.status, `POST ${ENABLE_PATH} answered: ${enabled.text.slice(0, 300)}`).toBe(200);
}

test("the first backstop pass records where history ends and imports nothing, and a later one imports", async ({
  isolatedHarness,
}) => {
  // A Cove pair, a Whisparr container, and two backstop passes between them. The default per-test
  // budget covers none of it.
  test.setTimeout(900_000);

  const api = createApiClient(
    () => isolatedHarness.baseUrl,
    () => isolatedHarness.token,
  );

  const whisparr = await startWhisparr({
    network: isolatedHarness.container.getNetworkNames()[0],
    generations: ["v3"],
    rootFolder: WHISPARR_ROOT,
    seedHistory: { count: SEEDED_ROWS },
  });

  try {
    const whisparrApi = whisparr.apiFor("v3");

    // Which stored event type this instance renders as the event the product acts on, taken from the
    // instance's own answer rather than named here. The extension switches on the string; a build
    // rendering another would leave everything below about a channel that acts on nothing.
    const importedEventType = Number(
      Object.entries(whisparr.v3.history.eventTypeNames).find(
        ([, rendered]) => rendered === IMPORTED_EVENT_TYPE,
      )?.[0],
    );
    expect(
      Number.isInteger(importedEventType),
      `no seeded row rendered as ${IMPORTED_EVENT_TYPE}; the instance rendered ${JSON.stringify(whisparr.v3.history.eventTypeNames)}`,
    ).toBe(true);

    // A past the backstop COULD import from: an import record naming a real file that really sits
    // under a Cove root. Without it "the first pass imported nothing" holds for reasons that have
    // nothing to do with the code, and the assertion is one no bug could ever fail.
    const replayable = `whisparr/${randomUUID()}.mp4`;
    const replayablePath = await placeVideoUnregistered({
      container: isolatedHarness.container,
      destPath: `${COVE_ROOT}/${replayable}`,
    });
    await whisparr.seedHistory("v3", {
      count: 1,
      eventTypes: [importedEventType],
      data: [{ importedPath: `${WHISPARR_ROOT}/${replayable}` }],
      expectedTotal: PAST_ROWS,
    });

    const saved = await api.put(SETTINGS_PATH, {
      selectedGeneration: "v3",
      v3: {
        address: whisparr.v3.internalBaseUrl,
        keyWrite: "replace",
        apiKey: whisparr.apiKey,
      },
      v2: null,
    });
    expect(saved.status, `saving settings failed: ${saved.text.slice(0, 300)}`).toBe(200);

    // The interval has no control in the settings page, so it is written through Cove's own
    // extension-data route. Driving it to the floor is what makes the whole backstop path reachable
    // inside a container that cannot fake a clock.
    const configured = await storedOptions(api);
    expect(
      configured.V3?.BackstopWatermarkUtc ?? null,
      "a mark was stored before any pass had run",
    ).toBeNull();
    await writeOptions(api, { ...configured, BackstopIntervalSeconds: FLOOR_SECONDS });

    // ACT ONE. The negative first: without it the reading after the pass could have been true all
    // along, and this spec would pass with the whole walk deleted.
    const coveBefore = await videosIn(api);
    expect(
      coveBefore.map((video) => video.id),
      `Cove already held ${coveBefore.length} video(s) before the first pass`,
    ).toEqual([]);

    // The instance as it stands before any pass has run. Taken here rather than at the start, so
    // what it measures is the backstop's effect and not the settings save's.
    const before = await instanceState(whisparrApi);
    expect(before.historyRecords, "the instance was seeded with no past to speak of").toBe(
      PAST_ROWS,
    );

    await restartWorker(api);

    const afterFirstPass = await pollUntil(
      () => readOptions(api),
      (options) => Boolean(options?.V3?.BackstopWatermarkUtc),
      {
        timeoutMs: WATERMARK_BUDGET_MS,
        intervalMs: 2_000,
        label: "the first backstop pass to record where the instance's history ends",
      },
    );

    // Both readings. One of the records the pass just walked past names a file Cove can see, so a
    // pass that replayed its instance's history would have registered it; the mark advancing is what
    // separates "did nothing" from "did the right nothing".
    expect(
      Date.parse(afterFirstPass.V3.BackstopWatermarkUtc),
      "the recorded mark is not a readable instant",
    ).not.toBeNaN();
    expect(
      await videosIn(api),
      "the first pass after connecting imported something, which is a bulk replay",
    ).toEqual([]);

    // ACT TWO. A SECOND file, named by a record newer than the mark. Which of the two files arrives
    // is what tells a walk that stopped at the mark from one that read past it.
    const tail = `whisparr/${randomUUID()}.mp4`;
    const covePath = await placeVideoUnregistered({
      container: isolatedHarness.container,
      destPath: `${COVE_ROOT}/${tail}`,
    });

    // One row, of the one kind, so it is the NEWEST row the instance holds. The seeder spaces rows a
    // minute apart from now, so a second kind would push the import row behind the mark act one just
    // recorded and the walk would rightly never reach it.
    const laterRows = 1;
    await whisparr.seedHistory("v3", {
      count: laterRows,
      eventTypes: [importedEventType],
      data: [{ importedPath: `${WHISPARR_ROOT}/${tail}` }],
      expectedTotal: PAST_ROWS + laterRows,
    });

    // A refusal reported as a poll timeout names the wrong cause, so the extension's own record of
    // what its passes did is read out and attached when nothing arrives.
    const registered = await pollUntil(
      () => videoPathsIn(api),
      (paths) => paths.includes(covePath),
      {
        timeoutMs: IMPORT_BUDGET_MS,
        intervalMs: 2_000,
        label: "Cove to hold the file the later record named",
      },
    ).catch(async (cause) => {
      const recorded = await readOptions(api);
      const logged = await tailContainerLog(isolatedHarness.container, { lines: 40 });
      // The exact page the walk reads. A record the pass should have acted on and a page it never
      // saw both arrive as "nothing was imported", and they are different failures.
      const page = await whisparrApi.get(
        "/api/v3/history?page=1&pageSize=50&sortKey=date&sortDirection=descending",
      );
      const walked = (page.json?.records ?? []).map((record) => ({
        date: record.date,
        eventType: record.eventType,
        data: record.data,
      }));
      throw new Error(
        `${cause.message}\nThe extension recorded: mark ${recorded?.V3?.BackstopWatermarkUtc}, ` +
          `health ${JSON.stringify(recorded?.ImportHealth)}, ` +
          `refusals ${JSON.stringify(recorded?.ImportRefusals)}. ` +
          `The reported path was ${WHISPARR_ROOT}/${tail} and the file is at ${covePath}.\n` +
          `The page the walk reads holds:\n${JSON.stringify(walked, null, 2)}\n` +
          `The host's log tail:\n${logged}`,
      );
    });

    // Nothing beyond the two files a record named. The record sitting exactly ON the mark is read
    // again by design — the stop rule takes a record at the mark rather than skipping it — so the
    // first file may arrive on this pass too; anything else would be a walk reading past the mark.
    expect(
      registered.filter((path) => path !== covePath && path !== replayablePath),
      "the backstop imported a file no record it should have read ever named",
    ).toEqual([]);

    // ACT THREE. The scene the library now holds, arriving again at a THIRD path through the
    // backstop alone. Both seeded records hang off the instance's one library entry, so every
    // arrival on this channel names the same scene.
    const declaredIdentifier = await sceneIdentifierIn(whisparrApi);
    const held = await videosIn(api);
    expect(
      held.map((video) => video.id),
      "the backstop stood up an item per file instead of one per scene, so the upgrade below has " +
        "no single item to join",
    ).toHaveLength(1);

    const itemId = held[0].id;
    const identified = await videoDetail(api, itemId);
    expect(
      identified.remoteIds,
      "the backstop registered the file without its identity, so nothing later can match on it",
    ).toEqual([{ endpoint: STASHDB_ENDPOINT, remoteId: declaredIdentifier }]);

    const upgrade = `whisparr/${randomUUID()}.mp4`;
    const upgradePath = await placeVideoUnregistered({
      container: isolatedHarness.container,
      destPath: `${COVE_ROOT}/${upgrade}`,
    });
    const upgradeRows = 1;
    await whisparr.seedHistory("v3", {
      count: upgradeRows,
      eventTypes: [importedEventType],
      data: [{ importedPath: `${WHISPARR_ROOT}/${upgrade}` }],
      expectedTotal: PAST_ROWS + laterRows + upgradeRows,
    });

    // Either settled outcome ends the wait, so the failure this act exists to catch — a second item
    // rather than a second file row — is reported as itself instead of as a poll timeout.
    const afterUpgrade = await pollUntil(
      async () => ({ item: await videoDetail(api, itemId), all: await videosIn(api) }),
      (seen) =>
        (seen.item.files ?? []).some((file) => file.path === upgradePath) || seen.all.length > 1,
      {
        timeoutMs: IMPORT_BUDGET_MS,
        intervalMs: 2_000,
        label: "the item to hold the file the later record named, or a second item to appear",
      },
    );

    expect(
      afterUpgrade.all.map((video) => video.id),
      "a scene arriving through the backstop alone created a second item beside the one the " +
        "library already held",
    ).toEqual([itemId]);

    // What separates "did not duplicate" from "did nothing at all": had the pass not acted, the
    // path below would be on no item anywhere.
    expect(
      afterUpgrade.item.files?.map((file) => file.path),
      "the backstop did not attach the new file to the item the library already had",
    ).toContain(upgradePath);
    expect(
      afterUpgrade.item.remoteIds,
      "the re-point added a second identity row for one source",
    ).toEqual([{ endpoint: STASHDB_ENDPOINT, remoteId: declaredIdentifier }]);

    // The backstop mutates the instance not at all. Its history holds exactly the rows this spec
    // seeded, and its notification list is the one it had before any pass ran.
    const after = await instanceState(whisparrApi);
    expect(
      after.historyRecords,
      "the instance's history grew by something this spec did not seed",
    ).toBe(PAST_ROWS + laterRows + upgradeRows);
    expect(after.notifications, "a backstop pass changed the instance's notifications").toBe(
      before.notifications,
    );
  } finally {
    // Before the harness's own, which the isolated fixture runs after this test: the daemon refuses
    // to remove a network a container still holds an endpoint on.
    await whisparr.stop();
  }
});

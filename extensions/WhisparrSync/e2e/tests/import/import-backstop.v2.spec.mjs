// The backstop pass reading v2's history, which it reads differently from v3's.
//
// Its v3 sibling covers what a pass does: the first records where history ends and imports nothing,
// a later one imports what arrived after the mark. That behaviour is one implementation. What is NOT
// one implementation is the reading underneath it. The history route is called through a different
// generated client per version, and a record's identity is taken from `episode.tvdbId` here where v3
// takes it from `movie.stashId`.
//
// So this drives the same pass on v2 and asserts the two things that differ: that the walk reads
// this version's history at all, and that what it imports carries the identity this version's own
// records name it by.
import {
  test as base,
  expect,
  createApiClient,
  isolatedHarnessFixture,
} from "@cove-extensions/e2e";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { placeVideoUnregistered } from "@cove-extensions/e2e/seed-media";
import { SEEDED_EPISODE_TVDB_ID, startWhisparr } from "@cove-extensions/e2e/whisparr";
import { randomUUID } from "node:crypto";

import {
  COVE_ROOT,
  DATA_ROUTE,
  DISABLE_ROUTE,
  ENABLE_ROUTE,
  OPTIONS_KEY,
  SETTINGS_ROUTE,
  THEPORNDB_ENDPOINT,
  WHISPARR_ROOT,
} from "../../lib/contract.mjs";
import { WHISPARR_SYNC_EXTENSION } from "../../lib/whisparr-sync-fixtures.mjs";

// What this version renders an import as. Transcribed rather than read off the instance: the walk
// selects on this string, so a spec deriving it would select on whatever the walk selects on. The
// NUMBER behind it is the instance's own and differs between the two versions, so that is read back
// from the seed rather than written down here.
const IMPORTED_EVENT_TYPE = "downloadFolderImported";

const SEEDED_ROWS = 3;

// The floor this product clamps the interval to, so a pass follows a restart without a long wait.
const FLOOR_SECONDS = 60;

const WATERMARK_BUDGET_MS = 240_000;
const IMPORT_BUDGET_MS = 240_000;

const test = base.extend({
  isolatedHarness: isolatedHarnessFixture(WHISPARR_SYNC_EXTENSION),
});

async function storedOptions(api) {
  const held = await api.get(DATA_ROUTE);
  expect(held.status, `GET ${DATA_ROUTE} answered: ${held.text.slice(0, 300)}`).toBe(200);
  return JSON.parse(held.json?.[OPTIONS_KEY] ?? "{}");
}

async function writeOptions(api, options) {
  const saved = await api.put(`${DATA_ROUTE}/${OPTIONS_KEY}`, JSON.stringify(options));
  expect(saved.status, `PUT the options key answered: ${saved.text.slice(0, 300)}`).toBe(200);
}

async function videosIn(api) {
  const listed = await api.get("/api/videos?perPage=200");
  expect(listed.status, `GET /api/videos answered: ${listed.text.slice(0, 300)}`).toBe(200);
  return listed.json?.items ?? [];
}

/** Stops and starts the worker, which is what makes a pass run without waiting out an interval. */
async function restartWorker(api) {
  const disabled = await api.post(DISABLE_ROUTE);
  expect(disabled.status, `POST ${DISABLE_ROUTE} answered: ${disabled.text.slice(0, 300)}`).toBe(
    200,
  );
  const enabled = await api.post(ENABLE_ROUTE);
  expect(enabled.status, `POST ${ENABLE_ROUTE} answered: ${enabled.text.slice(0, 300)}`).toBe(200);
}

const fileAt = (video, path) => (video.files ?? []).some((file) => file.path === path);

test("a v2 record the walk reads imports the file it names, under this version's identity", async ({
  isolatedHarness,
}) => {
  test.setTimeout(900_000);

  const api = createApiClient(
    () => isolatedHarness.baseUrl,
    () => isolatedHarness.token,
  );

  const whisparr = await startWhisparr({
    network: isolatedHarness.container.getNetworkNames()[0],
    generations: ["v2"],
    rootFolder: WHISPARR_ROOT,
    seedHistory: { count: SEEDED_ROWS },
  });

  try {
    // The code this instance renders the imported event under. Read off the seed's own read-back:
    // the two versions number their event types differently, and a row seeded under the other one's
    // number is a row the walk correctly ignores.
    const importedEventType = Number(
      Object.entries(whisparr.v2.history.eventTypeNames).find(
        ([, rendered]) => rendered === IMPORTED_EVENT_TYPE,
      )?.[0],
    );
    expect(
      Number.isInteger(importedEventType),
      `no seeded row rendered as ${IMPORTED_EVENT_TYPE}; the instance rendered ${JSON.stringify(whisparr.v2.history.eventTypeNames)}`,
    ).toBe(true);

    const saved = await api.put(SETTINGS_ROUTE, {
      selectedGeneration: "v2",
      v3: null,
      v2: {
        address: whisparr.v2.internalBaseUrl,
        keyWrite: "replace",
        apiKey: whisparr.apiKey,
      },
    });
    expect(saved.status, `saving settings failed: ${saved.text.slice(0, 300)}`).toBe(200);

    const configured = await storedOptions(api);
    expect(
      configured.V2?.BackstopWatermarkUtc ?? null,
      "a mark was stored before any pass had run",
    ).toBeNull();
    await writeOptions(api, { ...configured, BackstopIntervalSeconds: FLOOR_SECONDS });

    expect(await videosIn(api), "Cove already held a video before any pass had run").toEqual([]);

    // The first pass records where this version's history ends. That it records anything is itself
    // the evidence that the walk read a v2 instance: the route goes through a different generated
    // client here, and one that answered nothing would leave the mark unset.
    await restartWorker(api);
    const afterFirstPass = await pollUntil(
      () => storedOptions(api),
      (options) => Boolean(options?.V2?.BackstopWatermarkUtc),
      {
        timeoutMs: WATERMARK_BUDGET_MS,
        intervalMs: 2_000,
        label: "the first pass to record where this version's history ends",
      },
    );
    expect(
      Date.parse(afterFirstPass.V2.BackstopWatermarkUtc),
      "the recorded mark is not a readable instant",
    ).not.toBeNaN();
    expect(
      await videosIn(api),
      "the first pass after connecting imported something, which is a bulk replay",
    ).toEqual([]);

    // A record arriving after the mark, naming a file the library holds.
    const tail = `whisparr/${randomUUID()}.mp4`;
    const covePath = await placeVideoUnregistered({
      container: isolatedHarness.container,
      destPath: `${COVE_ROOT}/${tail}`,
    });
    await whisparr.seedHistory("v2", {
      count: 1,
      eventTypes: [importedEventType],
      data: [{ importedPath: `${WHISPARR_ROOT}/${tail}` }],
      expectedTotal: SEEDED_ROWS + 1,
    });

    const registered = await pollUntil(
      () => videosIn(api),
      (videos) => videos.some((video) => fileAt(video, covePath)),
      {
        timeoutMs: IMPORT_BUDGET_MS,
        intervalMs: 2_000,
        label: "Cove to hold the file the later record named",
      },
    );

    const held = registered.find((video) => fileAt(video, covePath));
    const whole = await api.get(`/api/videos/${String(held.id)}`);
    expect(whole.status, `GET the imported video answered: ${whole.text.slice(0, 300)}`).toBe(200);

    // The half only this version can show. The identity travels on the record's episode here, so an
    // item stamped with it is evidence the walk read THIS version's shape rather than looking for
    // the other one's and finding nothing.
    expect(
      whole.json.remoteIds,
      "the imported item does not carry the identifier this version's record named it by",
    ).toEqual([{ endpoint: THEPORNDB_ENDPOINT, remoteId: SEEDED_EPISODE_TVDB_ID }]);
  } finally {
    await whisparr.stop();
  }
});

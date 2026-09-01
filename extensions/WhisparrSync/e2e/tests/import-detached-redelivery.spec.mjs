// A redelivery naming a path this extension itself detached, driven through the real host.
//
// The detached row is not fabricated here: it is produced by the product's own Replace behaviour,
// which clears the video key on every superseded file row. So the input act three delivers is one a
// user reaches by running Whisparr and Cove together, not one a test invented.
//
// Act one is the control that makes everything after it readable. Without a file that really
// imported, "the redelivery broke nothing" and "nothing could ever be imported" are the same
// observation.
//
// Whether the delivery names an identifier is what decides the outcome, so act three drives both:
// without one there is no item to hand the host and the row it finds stays unclaimed, and with one
// the same path is re-attached. Only the first is the input the host answers by throwing.
//
// The last act drives the OTHER channel over the same state, because a throw out of the ingest does
// not merely answer one delivery badly: it escapes the walk, so the pass never reaches its mark, so
// every later pass re-reads the same page for ever. A history record carries no identifier at all,
// so that channel meets the unclaimed row every time. The mark advancing across a pass that read
// that record is what says the walk survived it.
import {
  expect,
  createApiClient,
  isolatedHarnessFixture,
  test as base,
} from "@cove-extensions/e2e";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { placeVideoUnregistered } from "@cove-extensions/e2e/seed-media";
import { startWhisparr } from "@cove-extensions/e2e/whisparr";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { randomUUID } from "node:crypto";
import { WHISPARR_SYNC_EXTENSION } from "../lib/whisparr-sync-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const SETTINGS_PATH = `/api/extensions/${EXTENSION_ID}/settings`;
const CALLBACK_PATH = `/api/extensions/${EXTENSION_ID}/callback`;
const CALLBACK_STATUS_PATH = `${CALLBACK_PATH}/status`;
const BANNER_PATH = `/api/extensions/${EXTENSION_ID}/import/banner`;
const DATA_PATH = `/api/extensions/${EXTENSION_ID}/data`;
const OPTIONS_KEY = "options";
const DISABLE_PATH = `/api/extensions/${EXTENSION_ID}/disable`;
const ENABLE_PATH = `/api/extensions/${EXTENSION_ID}/enable`;

// Transcribed by hand from the extension's own frozen constants.
const SECRET_HEADER = "X-Cove-Whisparr-Sync-Secret";
const SECRET_QUERY_PARAMETER = "s";

// Transcribed by hand from the delivery the pinned build made.
const V3_USER_AGENT = "Whisparr/3.3.8.1097 (alpine 3.23.5)";

// Transcribed by hand from the extension's own floor. A stored value below it is read as it.
const FLOOR_SECONDS = 30;

// The history route's own spelling of the event this product acts on, transcribed by hand from the
// extension's constant. The instance's own rendering is matched against it rather than read off it.
const IMPORTED_EVENT_TYPE = "downloadFolderImported";

// Deliberately different strings naming the same content: neither system can be resolved to the
// other by comparing them.
const WHISPARR_ROOT = "/whisparr-media";
const COVE_ROOT = "/data";

// How many rows the fixture seeds before anything else, spanning every event type it declares.
const SEEDED_ROWS = 3;

const IMPORT_BUDGET_MS = 120_000;
const WATERMARK_BUDGET_MS = 240_000;

const CAPTURED_DELIVERY = join(
  import.meta.dirname,
  "..",
  "..",
  "src",
  "WhisparrSync.Tests",
  "TestSupport",
  "Fixtures",
  "whisparr-v3-3.3.8.1097-webhook-import.json",
);

const test = base.extend({
  isolatedHarness: isolatedHarnessFixture(WHISPARR_SYNC_EXTENSION),
});

/**
 * The captured delivery, with only the file it names rewritten.
 *
 * `identified` drops the scene's own identifier, which an instance omits for a scene it holds no
 * stash id for. What the identifier decides here is whether the product can name an item for a file
 * row nothing claims.
 */
function deliveryNaming(reportedPath, size, { identified = true } = {}) {
  const body = JSON.parse(readFileSync(CAPTURED_DELIVERY, "utf8"));
  body.movieFile.path = reportedPath;
  body.movieFile.size = size;
  if (!identified) {
    delete body.movie.stashId;
  }
  return body;
}

async function callbackSecret(api) {
  const status = await api.get(CALLBACK_STATUS_PATH);
  expect(status.status, `GET ${CALLBACK_STATUS_PATH} answered: ${status.text.slice(0, 300)}`).toBe(
    200,
  );

  const secret = new URL(status.json.copyableAddress).searchParams.get(SECRET_QUERY_PARAMETER);
  expect(secret, `the copyable address carried no ${SECRET_QUERY_PARAMETER}`).toBeTruthy();
  return secret;
}

async function videosIn(api) {
  const listed = await api.get("/api/videos?perPage=200");
  expect(listed.status, `GET /api/videos answered: ${listed.text.slice(0, 300)}`).toBe(200);
  return listed.json?.items ?? [];
}

/**
 * One video as Cove holds it.
 *
 * Each read carries its own query so it gets its own output-cache entry: the host caches this route
 * briefly, and two reads a moment apart would otherwise be one answer.
 */
async function videoDetail(api, id) {
  const held = await api.get(`/api/videos/${id}?_=${randomUUID()}`);
  expect(held.status, `GET /api/videos/${id} answered: ${held.text.slice(0, 300)}`).toBe(200);
  return held.json;
}

/** The banner the settings page reads, as the extension answers it. */
async function bannerRoots(api) {
  const answered = await api.get(`${BANNER_PATH}?_=${randomUUID()}`);
  expect(answered.status, `GET ${BANNER_PATH} answered: ${answered.text.slice(0, 300)}`).toBe(200);
  return answered.json?.roots ?? [];
}

/** The extension's stored options blob, parsed, or null while the route is not answering. */
async function readOptions(api) {
  const data = await api.get(DATA_PATH);
  return data.status === 200 ? JSON.parse(data.json?.[OPTIONS_KEY] ?? "{}") : null;
}

async function storedOptions(api) {
  const options = await readOptions(api);
  expect(
    options,
    `GET ${DATA_PATH} did not answer with the extension's stored data`,
  ).not.toBeNull();
  return options;
}

async function writeOptions(api, change) {
  const written = await api.put(`${DATA_PATH}/${OPTIONS_KEY}`, JSON.stringify(change));
  expect(written.status, `PUT the options key answered: ${written.text.slice(0, 300)}`).toBe(200);
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

test("a redelivery naming a path the extension detached answers inside its contract, and the backstop over the same state keeps its place", async ({
  isolatedHarness,
}) => {
  // A Cove pair, a Whisparr container, and two backstop passes between them.
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

  /** Places one file under a Cove root and answers with its path and its size on disk. */
  async function place() {
    const tail = `whisparr/${randomUUID()}.mp4`;
    const covePath = await placeVideoUnregistered({
      container: isolatedHarness.container,
      destPath: `${COVE_ROOT}/${tail}`,
    });
    const { output } = await isolatedHarness.exec(["stat", "-c", "%s", covePath]);
    const size = Number(output.trim());
    expect(Number.isInteger(size) && size > 0, `stat reported ${output.trim()}`).toBe(true);
    return { tail, reportedPath: `${WHISPARR_ROOT}/${tail}`, covePath, size };
  }

  try {
    // Which stored event type this instance renders as the event the product acts on, taken from the
    // instance's own answer rather than named here.
    const importedEventType = Number(
      Object.entries(whisparr.v3.history.eventTypeNames).find(
        ([, rendered]) => rendered === IMPORTED_EVENT_TYPE,
      )?.[0],
    );
    expect(
      Number.isInteger(importedEventType),
      `no seeded row rendered as ${IMPORTED_EVENT_TYPE}; the instance rendered ${JSON.stringify(whisparr.v3.history.eventTypeNames)}`,
    ).toBe(true);

    const saved = await api.put(SETTINGS_PATH, {
      selectedGeneration: "v3",
      v3: { address: whisparr.v3.internalBaseUrl, keyWrite: "replace", apiKey: whisparr.apiKey },
      v2: null,
    });
    expect(saved.status, `saving settings failed: ${saved.text.slice(0, 300)}`).toBe(200);

    // Replace is what manufactures the detached row, so the whole spec is about a state only this
    // behaviour reaches.
    const chosen = await api.put(SETTINGS_PATH, {
      selectedGeneration: "v3",
      v3: null,
      v2: null,
      upgradeBehavior: "replace",
    });
    expect(chosen.status, `saving the upgrade behaviour failed: ${chosen.text.slice(0, 300)}`).toBe(
      200,
    );
    expect(chosen.json?.upgradeBehavior).toBe("replace");

    const secret = await callbackSecret(api);
    const asWhisparr = createApiClient(() => isolatedHarness.baseUrl, undefined, {
      headers: { [SECRET_HEADER]: secret, "User-Agent": V3_USER_AGENT },
    });

    const deliver = (file, options) =>
      asWhisparr.post(CALLBACK_PATH, deliveryNaming(file.reportedPath, file.size, options));

    expect(await videosIn(api), "Cove already held a video before the first delivery").toEqual([]);

    // ACT ONE. The control: a file that really imported. Everything after it reads as a change to
    // this, rather than as a system that never worked.
    const first = await place();
    const firstAnswer = await deliver(first);
    expect(
      firstAnswer.status,
      `the first delivery was refused, so nothing below is about the redelivery: ${firstAnswer.text.slice(0, 300)}`,
    ).toBe(200);

    const registered = await pollUntil(
      () => videosIn(api),
      (videos) => videos.length > 0,
      {
        timeoutMs: IMPORT_BUDGET_MS,
        intervalMs: 1_000,
        label: "Cove to hold the video the first delivery caused",
      },
    );
    expect(registered, "the first delivery produced more than one item").toHaveLength(1);
    const itemId = registered[0].id;

    // ACT TWO. The detached row, made by the product's own path: a second file for the same scene
    // re-points the item, and Replace clears the video key on the first file's row.
    const second = await place();
    const secondAnswer = await deliver(second);
    expect(
      secondAnswer.status,
      `the upgrade delivery was refused: ${secondAnswer.text.slice(0, 300)}`,
    ).toBe(200);

    const afterReplace = await pollUntil(
      async () => ({ item: await videoDetail(api, itemId), all: await videosIn(api) }),
      (seen) =>
        seen.item.files?.length === 1 &&
        seen.item.files[0].path === second.covePath &&
        seen.all.length === 1,
      {
        timeoutMs: IMPORT_BUDGET_MS,
        intervalMs: 1_000,
        label: "the item to hold only the file the upgrade named",
      },
    );
    expect(
      afterReplace.all.map((video) => video.id),
      "the upgrade created a second item instead of re-pointing the one that exists",
    ).toEqual([itemId]);

    // The row left the item; the file did not leave the disk. That is what makes the next act's
    // input reachable at all.
    const { exitCode: firstStillThere } = await isolatedHarness.exec([
      "test",
      "-f",
      first.covePath,
    ]);
    expect(firstStillThere, "the detach removed the superseded file from disk").toBe(0);

    // ACT THREE. The input that wedges: the FIRST path again, whose row is now present and claimed
    // by nothing, named by a delivery carrying no identifier - so there is no item to hand the host
    // and the row it finds stays unclaimed.
    const unnamed = await deliver(first, { identified: false });
    expect(
      unnamed.status,
      `an unidentified redelivery of a detached path answered outside the route's declared contract: ${unnamed.text.slice(0, 300)}`,
    ).toBe(200);

    // Refused, so the library is exactly where act two left it. Read after the answer rather than
    // polled: a change caused by this delivery would have to happen for the reading to be wrong.
    const untouched = await videoDetail(api, itemId);
    expect(
      untouched.files?.map((file) => file.path),
      "the unidentified redelivery changed which file the item holds",
    ).toEqual([second.covePath]);
    expect(await videosIn(api), "the unidentified redelivery created a second item").toHaveLength(
      1,
    );

    // The user has nothing to fix at a Whisparr root - the file is in the library already - so
    // nothing is said about one.
    expect(
      (await bannerRoots(api)).map((line) => line.root),
      "the refusal opened a banner line against a root the user did not misconfigure",
    ).toEqual([]);

    // The same path again, this time named by its identifier. That is the one argument that turns
    // the host's refusal into a re-attachment, so it separates "detached rows are never taken" from
    // "detached rows are taken when the product can say which item they belong to".
    const redelivered = await deliver(first);
    expect(
      redelivered.status,
      `the identified redelivery of a detached path was refused: ${redelivered.text.slice(0, 300)}`,
    ).toBe(200);

    const afterRedelivery = await pollUntil(
      async () => ({ item: await videoDetail(api, itemId), all: await videosIn(api) }),
      (seen) =>
        seen.item.files?.length === 1 &&
        seen.item.files[0].path === first.covePath &&
        seen.all.length === 1,
      {
        timeoutMs: IMPORT_BUDGET_MS,
        intervalMs: 1_000,
        label: "the item to hold the file the redelivery named",
      },
    );
    expect(
      afterRedelivery.all.map((video) => video.id),
      "the redelivery created a second item",
    ).toEqual([itemId]);

    // The user has nothing to fix at a Whisparr root, so nothing is said about one.
    expect(
      (await bannerRoots(api)).map((line) => line.root),
      "the redelivery opened a banner line against a root the user did not misconfigure",
    ).toEqual([]);

    // ACT FOUR. The other channel, over the row the redelivery has just detached in turn. A history
    // record names no identifier, so this is the detached path with nothing to re-point it to -
    // and a throw here does not answer one record badly, it freezes the mark for ever.
    const configured = await storedOptions(api);
    await writeOptions(api, { ...configured, BackstopIntervalSeconds: FLOOR_SECONDS });
    await restartWorker(api);

    const marked = await pollUntil(
      () => readOptions(api),
      (options) => Boolean(options?.V3?.BackstopWatermarkUtc),
      {
        timeoutMs: WATERMARK_BUDGET_MS,
        intervalMs: 2_000,
        label: "a backstop pass to record where the instance's history ends",
      },
    );
    const markBefore = Date.parse(marked.V3.BackstopWatermarkUtc);
    expect(markBefore, "the recorded mark is not a readable instant").not.toBeNaN();

    await whisparr.seedHistory("v3", {
      count: 1,
      eventTypes: [importedEventType],
      data: [{ importedPath: second.reportedPath }],
      expectedTotal: SEEDED_ROWS + 1,
    });

    const advanced = await pollUntil(
      () => readOptions(api),
      (options) =>
        Boolean(options?.V3?.BackstopWatermarkUtc) &&
        Date.parse(options.V3.BackstopWatermarkUtc) > markBefore,
      {
        timeoutMs: WATERMARK_BUDGET_MS,
        intervalMs: 2_000,
        label: "the stored mark to advance past the pass that read the detached record",
      },
    ).catch(async (cause) => {
      const recorded = await readOptions(api);
      throw new Error(
        `${cause.message}\nThe extension recorded: mark ${recorded?.V3?.BackstopWatermarkUtc}, ` +
          `health ${JSON.stringify(recorded?.ImportHealth)}, ` +
          `refusals ${JSON.stringify(recorded?.ImportRefusals)}.`,
      );
    });
    expect(
      Date.parse(advanced.V3.BackstopWatermarkUtc),
      "the pass that read the detached record left the mark where it was",
    ).toBeGreaterThan(markBefore);

    // The pass refused that record rather than importing it, so the library is where act three left
    // it: one item, holding the one file the redelivery named.
    expect(await videosIn(api), "the backstop created a second item").toHaveLength(1);
  } finally {
    // Before the harness's own, which the isolated fixture runs after this test: the daemon refuses
    // to remove a network a container still holds an endpoint on.
    await whisparr.stop();
  }
});

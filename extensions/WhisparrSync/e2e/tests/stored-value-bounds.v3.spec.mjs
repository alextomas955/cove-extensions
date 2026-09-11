// What this extension actually persists, enumerated after a run that exercised every ingest path
// this product has - not read off the code that writes it.
//
// The enumeration goes through Cove's OWN bulk extension-data route, which serialises every value an
// extension owns, whole, with no projection and no paging. That is the route the settings page reads,
// so a value that would fail there fails here in the same way and for the same reason.
//
// The size is the subject, not the content: a content assertion would go stale on every added
// setting and would have to be rewritten by whoever added it, which is how a check stops checking.
// The ceiling is a number transcribed by hand, and a bound that only held for a small run would be
// no bound at all - so the same enumeration is repeated after twenty further imports.
import {
  test as base,
  expect,
  createApiClient,
  isolatedHarnessFixture,
} from "@cove-extensions/e2e";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { placeVideoUnregistered } from "@cove-extensions/e2e/seed-media";
import {
  addCoveLibraryRoot,
  registerRootFolder,
  startWhisparr,
} from "@cove-extensions/e2e/whisparr";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { randomUUID } from "node:crypto";
import { WHISPARR_SYNC_EXTENSION } from "../lib/whisparr-sync-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const SETTINGS_PATH = `/api/extensions/${EXTENSION_ID}/settings`;
const CALLBACK_PATH = `/api/extensions/${EXTENSION_ID}/callback`;
const CALLBACK_STATUS_PATH = `${CALLBACK_PATH}/status`;
const DATA_PATH = `/api/extensions/${EXTENSION_ID}/data`;
const DISABLE_PATH = `/api/extensions/${EXTENSION_ID}/disable`;
const ENABLE_PATH = `/api/extensions/${EXTENSION_ID}/enable`;

// The one key this extension is expected to own. A second key is a finding rather than a detail: the
// route above returns every one of them together.
const OPTIONS_KEY = "options";

// A HAND-SET CEILING, not a measurement of what is stored today. A number derived from the current
// size would agree with the code forever and report nothing.
const STORED_BYTES_CEILING = 8192;

// How much the stored value may differ between the two enumerations. Hand-set, and far below what a
// per-file collection would add over the imports driven between them.
const GROWTH_SLACK_BYTES = 512;

const FURTHER_IMPORTS = 20;

// Transcribed by hand from the extension's own frozen constants and from the delivery the pinned
// build made.
const SECRET_HEADER = "X-Cove-Whisparr-Sync-Secret";
const SECRET_QUERY_PARAMETER = "s";
const V3_USER_AGENT = "Whisparr/3.3.8.1097 (alpine 3.23.5)";

// Transcribed by hand from the extension's own floor. A stored value below it is read as it.
const FLOOR_SECONDS = 30;

// The history route's own spelling of the event this product acts on, transcribed from the
// extension's constant.
const IMPORTED_EVENT_TYPE = "downloadFolderImported";

// How many rows the fixture seeds before the extension is pointed at the instance.
const SEEDED_ROWS = 3;

// Two roots the instance declares for itself, and two the harness declares. The Whisparr spellings
// name content Cove reaches by another name.
const WHISPARR_ROOT = "/whisparr-media";
const WHISPARR_OTHER_ROOT = "/whisparr-elsewhere";
const COVE_ROOT = "/data";
const COVE_ROOTS = ["/data", "/data2"];
const NESTED_COVE_ROOT = "/data/nested";

const PASS_BUDGET_MS = 240_000;
const IMPORT_BUDGET_MS = 180_000;

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
 * The delivery a real instance sent, naming one file and one scene.
 *
 * The scene identifier is rewritten per delivery. The captured body carries one, and reusing it
 * would make every delivery a redelivery of the same scene, so twenty imports would be one item
 * re-pointed twenty times.
 */
function deliveryNaming(reportedPath, size, sceneId) {
  const body = JSON.parse(readFileSync(CAPTURED_DELIVERY, "utf8"));
  body.movieFile.path = reportedPath;
  body.movieFile.size = size;
  body.movie.stashId = sceneId;
  return body;
}

/** Points the extension at the fixture instance and stores its key. */
async function configure(api, whisparr) {
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
}

/** Stores one upgrade behaviour, naming neither generation. */
async function chooseUpgradeBehavior(api, behavior) {
  const saved = await api.put(SETTINGS_PATH, {
    selectedGeneration: "v3",
    v3: null,
    v2: null,
    upgradeBehavior: behavior,
  });
  expect(saved.status, `saving the upgrade behaviour failed: ${saved.text.slice(0, 300)}`).toBe(
    200,
  );
  return saved.json?.upgradeBehavior;
}

/** This installation's own callback secret, read out of the address the page offers. */
async function callbackSecret(api) {
  const status = await api.get(CALLBACK_STATUS_PATH);
  expect(status.status, `GET ${CALLBACK_STATUS_PATH} answered: ${status.text.slice(0, 300)}`).toBe(
    200,
  );

  const secret = new URL(status.json.copyableAddress).searchParams.get(SECRET_QUERY_PARAMETER);
  expect(secret, `the copyable address carried no ${SECRET_QUERY_PARAMETER}`).toBeTruthy();
  return secret;
}

/**
 * Every video Cove holds.
 *
 * Each read carries its own query so it gets its own output-cache entry: the host caches briefly,
 * and two reads a moment apart would otherwise be one answer.
 */
async function videosIn(api) {
  const listed = await api.get(`/api/videos?perPage=200&_=${randomUUID()}`);
  expect(listed.status, `GET /api/videos answered: ${listed.text.slice(0, 300)}`).toBe(200);
  return listed.json?.items ?? [];
}

/** Everything the extension has stored, as Cove's own bulk route returns it. */
async function storedData(api) {
  const stored = await api.get(`${DATA_PATH}?_=${randomUUID()}`);
  expect(stored.status, `GET ${DATA_PATH} answered: ${stored.text.slice(0, 300)}`).toBe(200);
  return stored;
}

/** The stored options blob, parsed, or null while the route is not answering. */
async function readOptions(api) {
  const data = await api.get(`${DATA_PATH}?_=${randomUUID()}`);
  return data.status === 200 ? JSON.parse(data.json?.[OPTIONS_KEY] ?? "{}") : null;
}

/** Rewrites the stored options blob with `change` applied. */
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

/** The longest array anywhere in `value`, however deeply nested. */
function longestArrayIn(value) {
  if (Array.isArray(value)) {
    return Math.max(value.length, ...value.map(longestArrayIn), 0);
  }

  if (value !== null && typeof value === "object") {
    return Math.max(...Object.values(value).map(longestArrayIn), 0);
  }

  return 0;
}

test("what the extension persists is one bounded key, after a run that exercised every path", async ({
  isolatedHarness,
}) => {
  // A Cove pair, a Whisparr container, two backstop passes and better than twenty deliveries between
  // them. The default per-test budget covers none of it.
  test.setTimeout(1_800_000);

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
    await registerRootFolder(
      whisparr.v3.container,
      whisparr.apiFor("v3"),
      "v3",
      WHISPARR_OTHER_ROOT,
    );
    await configure(api, whisparr);
    const secret = await callbackSecret(api);

    // Its own client, carrying no Cove credential: the secret plus the agent are the whole of what a
    // real delivery presents.
    const asWhisparr = createApiClient(() => isolatedHarness.baseUrl, undefined, {
      headers: { [SECRET_HEADER]: secret, "User-Agent": V3_USER_AGENT },
    });

    /** Places one file under a Cove root and answers with what a delivery would report for it. */
    async function place(coveRoot = COVE_ROOT, tail = `whisparr/${randomUUID()}.mp4`) {
      const covePath = await placeVideoUnregistered({
        container: isolatedHarness.container,
        destPath: `${coveRoot}/${tail}`,
      });
      return { tail, covePath };
    }

    /** Posts one delivery, as a real instance would. */
    async function deliver(reportedPath, sceneId, why) {
      const delivered = await asWhisparr.post(
        CALLBACK_PATH,
        deliveryNaming(reportedPath, fileSize, sceneId),
      );
      // A diagnostic, not the evidence. A refused delivery surfacing later as a count that never
      // moved would name the wrong cause entirely.
      expect(delivered.status, `${why}: ${delivered.text.slice(0, 300)}`).toBeLessThan(400);
    }

    // Every placed copy comes from one fixture, so the size is read once off the file Cove can see.
    // A delivery reporting a made-up size would be exercising the refusal rather than the import.
    const sized = await place();
    const { output } = await isolatedHarness.exec(["stat", "-c", "%s", sized.covePath]);
    const fileSize = Number(output.trim());
    expect(
      Number.isInteger(fileSize) && fileSize > 0,
      `stat reported ${output.trim()} for ${sized.covePath}`,
    ).toBe(true);

    // The negative. Without it every assertion below could have been true from the start.
    expect(
      (await videosIn(api)).map((video) => video.id),
      "Cove already held a video before any delivery",
    ).toEqual([]);
    const initial = await readOptions(api);
    expect(initial?.ImportRefusals ?? [], "the extension already held refusals").toEqual([]);

    // ---- the backstop, twice ----
    // The interval has no control in the page, so it is written through Cove's own extension-data
    // route. Driving it to the floor is what makes the whole backstop path reachable in a container
    // that cannot fake a clock.
    await writeOptions(api, { ...initial, BackstopIntervalSeconds: FLOOR_SECONDS });
    await restartWorker(api);

    const firstPass = await pollUntil(
      () => readOptions(api),
      (options) => Boolean(options?.V3?.BackstopWatermarkUtc),
      {
        timeoutMs: PASS_BUDGET_MS,
        intervalMs: 2_000,
        label: "the first backstop pass to record where history ends",
      },
    );
    const firstMark = firstPass.V3.BackstopWatermarkUtc;

    // A record the second pass can take: an import naming a real file that really sits under a Cove
    // root. Against a past nothing could be taken from, a pass that walked and one that did not are
    // the same observation.
    const importedEventType = Number(
      Object.entries(whisparr.v3.history.eventTypeNames).find(
        ([, rendered]) => rendered === IMPORTED_EVENT_TYPE,
      )?.[0],
    );
    expect(
      Number.isInteger(importedEventType),
      `no seeded row rendered as ${IMPORTED_EVENT_TYPE}; the instance rendered ${JSON.stringify(whisparr.v3.history.eventTypeNames)}`,
    ).toBe(true);

    const replayable = await place();
    await whisparr.seedHistory("v3", {
      count: 1,
      eventTypes: [importedEventType],
      data: [{ importedPath: `${WHISPARR_ROOT}/${replayable.tail}` }],
      expectedTotal: SEEDED_ROWS + 1,
    });

    await pollUntil(
      () => readOptions(api),
      (options) => options?.V3?.BackstopWatermarkUtc !== firstMark,
      {
        timeoutMs: PASS_BUDGET_MS,
        intervalMs: 2_000,
        label: "a second backstop pass to move the mark past the first",
      },
    );

    // ---- imports, across both of the instance's roots ----
    // The file the size was read off is the first of them, so no placed file is left unregistered
    // beside the ones this run is counting.
    for (const file of [sized, await place()]) {
      await deliver(
        `${WHISPARR_ROOT}/${file.tail}`,
        randomUUID(),
        "an import delivery was refused",
      );
    }

    const underOtherRoot = await place();
    await deliver(
      `${WHISPARR_OTHER_ROOT}/${underOtherRoot.tail}`,
      randomUUID(),
      "the delivery under the instance's other root was refused",
    );

    // ---- an upgrade re-point, under both behaviours ----
    const upgradedScene = randomUUID();
    const upgradeFirst = await place();
    await deliver(
      `${WHISPARR_ROOT}/${upgradeFirst.tail}`,
      upgradedScene,
      "the delivery the upgrade re-points was refused",
    );

    const upgradeAdd = await place();
    await deliver(
      `${WHISPARR_ROOT}/${upgradeAdd.tail}`,
      upgradedScene,
      "the re-point under the attaching behaviour was refused",
    );

    expect(await chooseUpgradeBehavior(api, "replace")).toBe("replace");
    const upgradeReplace = await place();
    await deliver(
      `${WHISPARR_ROOT}/${upgradeReplace.tail}`,
      upgradedScene,
      "the re-point under the detaching behaviour was refused",
    );

    // ---- refusals, of the causes an end-to-end run can reach ----
    // A path under the instance's other root that is on no Cove disk. Three of them, which is one
    // more than that root's line holds, so the fixed-size design is exercised rather than described.
    for (let index = 0; index < 3; index++) {
      await deliver(
        `${WHISPARR_OTHER_ROOT}/${randomUUID()}.mp4`,
        randomUUID(),
        "a not-found delivery was rejected before the ingest read it",
      );
    }

    // A path under none of the roots the instance declares.
    for (let index = 0; index < 2; index++) {
      await deliver(
        `/declared-by-nobody/${randomUUID()}.mp4`,
        randomUUID(),
        "a delivery outside every reported root was rejected before the ingest read it",
      );
    }

    // The same tail under two Cove roots, one declared inside the other, so both candidates the
    // extension forms are really there and neither can be chosen.
    const ambiguousTail = `whisparr/${randomUUID()}.mp4`;
    await place(COVE_ROOT, ambiguousTail);
    const nested = await place(NESTED_COVE_ROOT, ambiguousTail);
    expect(
      await addCoveLibraryRoot(api, NESTED_COVE_ROOT, COVE_ROOTS),
      "Cove does not report the nested root that makes the delivery ambiguous",
    ).toContain(NESTED_COVE_ROOT);

    await deliver(
      `${WHISPARR_ROOT}/${ambiguousTail}`,
      randomUUID(),
      "the ambiguous delivery was rejected before the ingest read it",
    );

    // The second copy goes once the refusal is recorded. Left in place it is a file under a library
    // root that this run never registered, and every later count would have to allow for it.
    await isolatedHarness.exec(["rm", nested.covePath], { user: "root" });

    // The run did what it claims to have done, read off Cove rather than off the extension.
    const beforeTheRest = await videosIn(api);
    expect(
      beforeTheRest.length,
      "the run that is supposed to have driven several imports registered nothing",
    ).toBeGreaterThan(1);

    const outstanding = (await readOptions(api)).ImportRefusals ?? [];
    expect(
      outstanding.length,
      `the run that is supposed to have driven refusals recorded none: ${JSON.stringify(outstanding)}`,
    ).toBeGreaterThan(1);

    // ---- ENUMERATION ONE ----
    const first = await storedData(api);

    expect(
      Object.keys(first.json).sort(),
      "the extension owns a stored key beside the one bounded blob",
    ).toEqual([OPTIONS_KEY]);

    const firstBytes = Buffer.byteLength(first.json[OPTIONS_KEY], "utf8");
    expect(
      firstBytes,
      `the stored value is ${firstBytes} bytes, past the hand-set ceiling`,
    ).toBeLessThan(STORED_BYTES_CEILING);

    // Neither the key the instance is read with nor the secret the inbound route is authenticated by.
    // Both live in a table this extension owns, which this route cannot reach.
    expect(first.text, "the stored data carries the instance's API key").not.toContain(
      whisparr.apiKey,
    );
    expect(first.text, "the stored data carries the inbound callback secret").not.toContain(secret);

    // ---- twenty further imports ----
    // Placed and delivered one at a time, so no file sits under a library root unregistered while a
    // follow-up scan the previous import started is running.
    for (let index = 0; index < FURTHER_IMPORTS; index++) {
      const file = await place();
      await deliver(
        `${WHISPARR_ROOT}/${file.tail}`,
        randomUUID(),
        "one of the further deliveries was refused",
      );
    }

    const registered = await pollUntil(
      () => videosIn(api),
      (videos) => videos.length >= beforeTheRest.length + FURTHER_IMPORTS,
      {
        timeoutMs: IMPORT_BUDGET_MS,
        intervalMs: 2_000,
        label: `Cove to hold the ${FURTHER_IMPORTS} further items the deliveries caused`,
      },
    );
    expect(registered.length, "the further deliveries did not each register a distinct item").toBe(
      beforeTheRest.length + FURTHER_IMPORTS,
    );

    // ---- ENUMERATION TWO ----
    const second = await storedData(api);

    expect(
      Object.keys(second.json).sort(),
      "a stored key appeared over the further imports",
    ).toEqual([OPTIONS_KEY]);

    const secondBytes = Buffer.byteLength(second.json[OPTIONS_KEY], "utf8");
    expect(
      secondBytes,
      `the stored value is ${secondBytes} bytes after ${FURTHER_IMPORTS} further imports, past the hand-set ceiling`,
    ).toBeLessThan(STORED_BYTES_CEILING);

    // The ceiling alone would still pass for a value that grew by a line per file, so the growth is
    // bounded too: what is stored is the same size after twenty more imports as before them.
    expect(
      secondBytes,
      `the stored value grew from ${firstBytes} to ${secondBytes} bytes over ${FURTHER_IMPORTS} imports`,
    ).toBeLessThanOrEqual(firstBytes + GROWTH_SLACK_BYTES);

    // Nothing in it is a collection whose length tracks what was imported.
    const longest = longestArrayIn(JSON.parse(second.json[OPTIONS_KEY]));
    expect(
      longest,
      `a stored collection holds ${longest} entries, which is the shape a per-file journal has`,
    ).toBeLessThan(FURTHER_IMPORTS);

    expect(second.text, "the stored data carries the instance's API key").not.toContain(
      whisparr.apiKey,
    );
    expect(second.text, "the stored data carries the inbound callback secret").not.toContain(
      secret,
    );
  } finally {
    // Before the harness's own, which the isolated fixture runs after this test: the daemon refuses
    // to remove a network a container still holds an endpoint on.
    await whisparr.stop();
  }
});

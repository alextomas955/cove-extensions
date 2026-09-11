// The two deliveries that are not the happy path, against the real host: a reported file present at
// no Cove root, and one present under two.
//
// Both are asserted on the COVE side — that no item was created, and what the extension's own stored
// aggregate says afterwards. The callback's status says the request was well formed and nothing about
// whether anything was registered, so it is a diagnostic here and never the subject.
//
// The third delivery is the control. Removing one of the two copies leaves exactly one candidate, and
// the same body then imports and clears that root's line: without it, a run in which the extension
// did nothing at all would satisfy every assertion above it.
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

// Transcribed by hand from the extension's own frozen constants and from the delivery the pinned
// build made. Written out rather than imported, so a rename on either side has to be made twice.
const SECRET_HEADER = "X-Cove-Whisparr-Sync-Secret";
const SECRET_QUERY_PARAMETER = "s";
const V3_USER_AGENT = "Whisparr/3.3.8.1097 (alpine 3.23.5)";

// Transcribed from the blob the backend serializes, which its own test pins: the member names are
// PascalCase and the cause is camelCase.
const REFUSALS = "ImportRefusals";
const NOT_FOUND = "notFoundUnderAnyRoot";
const AMBIGUOUS = "ambiguousCandidates";

// Two roots the instance declares for itself, and the Cove roots the harness declares. The Whisparr
// spellings name content Cove reaches by another name, which is the deployment this resolution
// exists for.
const WHISPARR_ROOT = "/whisparr-media";
const WHISPARR_OTHER_ROOT = "/whisparr-elsewhere";
const COVE_ROOTS = ["/data", "/data2"];
const COVE_ROOT = "/data";
const NESTED_COVE_ROOT = "/data/nested";

const REFUSAL_BUDGET_MS = 60_000;
const IMPORT_BUDGET_MS = 120_000;

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

/** The delivery a real instance sent, with only the file it names rewritten. */
function deliveryNaming(reportedPath, size) {
  const body = JSON.parse(readFileSync(CAPTURED_DELIVERY, "utf8"));
  body.movieFile.path = reportedPath;
  body.movieFile.size = size;
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

/** Every video Cove holds. */
async function videosIn(api) {
  const listed = await api.get("/api/videos?perPage=200");
  expect(listed.status, `GET /api/videos answered: ${listed.text.slice(0, 300)}`).toBe(200);
  return listed.json?.items ?? [];
}

/**
 * The refusal aggregate the extension has stored, read through Cove's own bulk data route — the same
 * route the settings page reads, so an oversized value would fail here as it fails there.
 */
async function refusalsIn(api) {
  const stored = await api.get(DATA_PATH);
  expect(stored.status, `GET ${DATA_PATH} answered: ${stored.text.slice(0, 300)}`).toBe(200);

  const options = stored.json?.options;
  return options ? (JSON.parse(options)[REFUSALS] ?? []) : [];
}

/** One root's line, or undefined while that root has none. */
const lineFor = (refusals, root) => refusals.find((entry) => entry.Root === root);

test("a reported file at no Cove root, and one at two, are each refused and counted", async ({
  isolatedHarness,
}) => {
  // A Cove pair, a Whisparr container and three deliveries between them. The default per-test budget
  // covers none of it.
  test.setTimeout(900_000);

  const api = createApiClient(
    () => isolatedHarness.baseUrl,
    () => isolatedHarness.token,
  );

  const whisparr = await startWhisparr({
    network: isolatedHarness.container.getNetworkNames()[0],
    generations: ["v3"],
  });

  try {
    for (const root of [WHISPARR_ROOT, WHISPARR_OTHER_ROOT]) {
      await registerRootFolder(whisparr.v3.container, whisparr.apiFor("v3"), "v3", root);
    }
    await configure(api, whisparr);
    const secret = await callbackSecret(api);

    // Its own client, carrying no Cove credential: Whisparr holds none, and the secret plus the agent
    // are the whole of what a real delivery presents.
    const asWhisparr = createApiClient(() => isolatedHarness.baseUrl, undefined, {
      headers: { [SECRET_HEADER]: secret, "User-Agent": V3_USER_AGENT },
    });

    /** Posts the captured body, naming `reportedPath`, as a real instance would. */
    const deliver = async (reportedPath, size) => {
      const delivered = await asWhisparr.post(CALLBACK_PATH, deliveryNaming(reportedPath, size));
      // A diagnostic, not the evidence: a refused delivery surfacing as a poll timeout would name the
      // wrong cause entirely.
      expect(
        delivered.status,
        `the callback refused the delivery, so nothing below is about the ingest: ${delivered.text.slice(0, 300)}`,
      ).toBeLessThan(400);
    };

    // Nothing is stored and nothing is held, before any delivery. Without this the assertions below
    // could have been true all along.
    expect(await refusalsIn(api), "the extension already held refusals").toEqual([]);
    expect(
      (await videosIn(api)).map((video) => video.id),
      "Cove already held a video before any delivery",
    ).toEqual([]);

    // ---- 1. a reported file present at no Cove root ----
    const absentTail = `${randomUUID()}.mp4`;
    await deliver(`${WHISPARR_OTHER_ROOT}/${absentTail}`, 4096);

    const notFound = await pollUntil(
      () => refusalsIn(api),
      (refusals) => lineFor(refusals, WHISPARR_OTHER_ROOT) !== undefined,
      {
        timeoutMs: REFUSAL_BUDGET_MS,
        intervalMs: 1_000,
        label: `a refusal counted against ${WHISPARR_OTHER_ROOT}`,
      },
    );

    const absent = lineFor(notFound, WHISPARR_OTHER_ROOT);
    expect(absent.CountSinceLastSuccess).toBe(1);
    expect(absent.NewestPaths.map((entry) => entry.Cause)).toEqual([NOT_FOUND]);
    expect(absent.NewestPaths[0].Path).toBe(`${WHISPARR_OTHER_ROOT}/${absentTail}`);
    expect(
      (await videosIn(api)).map((video) => video.id),
      "a file at no Cove root was registered anyway",
    ).toEqual([]);

    // ---- 2. a reported file present under two Cove roots ----
    // One tail, two copies: one under the harness's own root and one under a root declared inside it,
    // so both candidates the extension forms are really there.
    const tail = `${randomUUID()}.mp4`;
    const outer = await placeVideoUnregistered({
      container: isolatedHarness.container,
      destPath: `${COVE_ROOT}/${tail}`,
    });
    const inner = await placeVideoUnregistered({
      container: isolatedHarness.container,
      destPath: `${NESTED_COVE_ROOT}/${tail}`,
    });
    const roots = await addCoveLibraryRoot(api, NESTED_COVE_ROOT, COVE_ROOTS);
    expect(
      roots,
      "Cove does not report the nested root that makes the delivery ambiguous",
    ).toContain(NESTED_COVE_ROOT);

    const { output } = await isolatedHarness.exec(["stat", "-c", "%s", outer]);
    const size = Number(output.trim());
    expect(Number.isInteger(size) && size > 0, `stat reported ${output.trim()} for ${outer}`).toBe(
      true,
    );

    expect(
      (await videosIn(api)).map((video) => video.id),
      "Cove already held a video before the ambiguous delivery",
    ).toEqual([]);

    await deliver(`${WHISPARR_ROOT}/${tail}`, size);

    const ambiguous = await pollUntil(
      () => refusalsIn(api),
      (refusals) => lineFor(refusals, WHISPARR_ROOT) !== undefined,
      {
        timeoutMs: REFUSAL_BUDGET_MS,
        intervalMs: 1_000,
        label: `a refusal counted against ${WHISPARR_ROOT}`,
      },
    );

    expect(lineFor(ambiguous, WHISPARR_ROOT).NewestPaths.map((entry) => entry.Cause)).toEqual([
      AMBIGUOUS,
    ]);
    expect(
      (await videosIn(api)).map((video) => video.id),
      "one of two candidates was imported rather than refused",
    ).toEqual([]);

    // The other root's line is untouched by a refusal under this one.
    expect(lineFor(ambiguous, WHISPARR_OTHER_ROOT).NewestPaths.map((entry) => entry.Cause)).toEqual(
      [NOT_FOUND],
    );

    // ---- 3. the control: one copy removed, the same delivery imports ----
    await isolatedHarness.exec(["rm", inner], { user: "root" });

    await deliver(`${WHISPARR_ROOT}/${tail}`, size);

    const registered = await pollUntil(
      () => videosIn(api),
      (videos) => videos.length > 0,
      {
        timeoutMs: IMPORT_BUDGET_MS,
        intervalMs: 1_000,
        label: "Cove to hold the video the unambiguous delivery caused",
      },
    );
    expect(registered, "the delivery produced more than one item").toHaveLength(1);

    const held = await api.get(`/api/videos/${registered[0].id}`);
    expect(held.status, `GET the imported video answered: ${held.text.slice(0, 300)}`).toBe(200);
    expect(
      held.json.files?.map((file) => file.path),
      "the item Cove created is not at the path this extension verified",
    ).toEqual([outer]);

    // That root's line is cleared by its own success, and the other root's survives it.
    const cleared = await pollUntil(
      () => refusalsIn(api),
      (refusals) => lineFor(refusals, WHISPARR_ROOT) === undefined,
      {
        timeoutMs: REFUSAL_BUDGET_MS,
        intervalMs: 1_000,
        label: `${WHISPARR_ROOT}'s line to be cleared by its own success`,
      },
    );
    expect(
      cleared.map((entry) => entry.Root),
      "a success under one root did not leave the other root's line alone",
    ).toEqual([WHISPARR_OTHER_ROOT]);
  } finally {
    // Before the harness's own, which the isolated fixture runs after this test: the daemon refuses
    // to remove a network a container still holds an endpoint on.
    await whisparr.stop();
  }
});

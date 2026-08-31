// One authenticated Whisparr delivery becomes one real Cove library item, against the real host.
//
// Every assertion is on the COVE side. The callback's own status says the request was well formed
// and says nothing about whether anything was registered, and this product's whole failure mode is a
// pass that read nothing — so the status is deliberately not the subject here.
//
// The delivery body is the one a real instance sent, read from the payload capture committed beside
// the backend tests. Only the file path and its size are rewritten, to name the file this spec
// placed; every other member is exactly what Whisparr delivers. A body assembled by hand would test
// a shape nothing sends, which is the failure the capture exists to prevent.
//
// A live Whisparr is part of the fixture and not decoration: neither generation's import event names
// a root folder, so the extension reads the reporting instance's declared roots off the instance
// itself. Without one running and configured there is no tail to take, and the ingest refuses.
import {
  test as base,
  expect,
  createApiClient,
  isolatedHarnessFixture,
} from "@cove-extensions/e2e";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { placeVideoUnregistered } from "@cove-extensions/e2e/seed-media";
import { registerRootFolder, startWhisparr } from "@cove-extensions/e2e/whisparr";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { randomUUID } from "node:crypto";
import { WHISPARR_SYNC_EXTENSION } from "../lib/whisparr-sync-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const SETTINGS_PATH = `/api/extensions/${EXTENSION_ID}/settings`;
const CALLBACK_PATH = `/api/extensions/${EXTENSION_ID}/callback`;
const CALLBACK_STATUS_PATH = `${CALLBACK_PATH}/status`;

// Transcribed by hand from the extension's own frozen constants. Written out rather than imported,
// so a rename on either side has to be made in both places.
const SECRET_HEADER = "X-Cove-Whisparr-Sync-Secret";
const SECRET_QUERY_PARAMETER = "s";

// Transcribed by hand from the delivery the pinned build made. The extension decides where in a body
// to read from this, so a spec sending another agent would be exercising a different branch.
const V3_USER_AGENT = "Whisparr/3.3.8.1097 (alpine 3.23.5)";

// The root the fixture instance declares for itself, and the root Cove declares in the compose file.
// They are deliberately DIFFERENT strings naming the same content, which is the deployment this
// resolution exists for: neither system can be resolved to the other by comparing them.
const WHISPARR_ROOT = "/whisparr-media";
const COVE_ROOT = "/data";

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

/** Every video Cove holds, with the file paths it holds them at. */
async function videosIn(api) {
  const listed = await api.get("/api/videos?perPage=200");
  expect(listed.status, `GET /api/videos answered: ${listed.text.slice(0, 300)}`).toBe(200);
  return listed.json?.items ?? [];
}

test("an authenticated delivery registers the file this extension verified on disk", async ({
  isolatedHarness,
}) => {
  // A Cove pair, a Whisparr container and a real import between them. The default per-test budget
  // covers none of it.
  test.setTimeout(600_000);

  const api = createApiClient(
    () => isolatedHarness.baseUrl,
    () => isolatedHarness.token,
  );

  const whisparr = await startWhisparr({
    network: isolatedHarness.container.getNetworkNames()[0],
    generations: ["v3"],
  });

  try {
    await registerRootFolder(whisparr.v3.container, whisparr.apiFor("v3"), "v3", WHISPARR_ROOT);
    await configure(api, whisparr);
    const secret = await callbackSecret(api);

    // Unique per run, so a repeat against a reused image cannot pass on a previous run's item.
    const tail = `whisparr/${randomUUID()}.mp4`;
    const covePath = await placeVideoUnregistered({
      container: isolatedHarness.container,
      destPath: `${COVE_ROOT}/${tail}`,
    });

    // The negative first. Without it the assertion after the delivery could have been true all
    // along, and this spec would pass with the whole ingest deleted.
    const before = await videosIn(api);
    expect(
      before.map((video) => video.id),
      `Cove already held ${before.length} video(s) before the delivery`,
    ).toEqual([]);

    // The file's real size on disk, read off the file Cove can see. The delivery reports a size and
    // the extension refuses a candidate whose length disagrees, so a spec that reported a made-up
    // one would be testing the refusal rather than the import.
    const { output } = await isolatedHarness.exec(["stat", "-c", "%s", covePath]);
    const size = Number(output.trim());
    expect(
      Number.isInteger(size) && size > 0,
      `stat reported ${output.trim()} for ${covePath}`,
    ).toBe(true);

    // Its own client, carrying no Cove credential: Whisparr holds none, and the secret plus the
    // agent are the whole of what a real delivery presents.
    const asWhisparr = createApiClient(() => isolatedHarness.baseUrl, undefined, {
      headers: { [SECRET_HEADER]: secret, "User-Agent": V3_USER_AGENT },
    });
    const delivered = await asWhisparr.post(
      CALLBACK_PATH,
      deliveryNaming(`${WHISPARR_ROOT}/${tail}`, size),
    );

    // A diagnostic, not the evidence. This spec's subject is what Cove holds afterwards, and a 200
    // here would say only that the request was well formed — but a refusal reported as a poll
    // timeout would name the wrong cause, so a refusal is surfaced as one.
    expect(
      delivered.status,
      `the callback refused the delivery, so nothing below is about the ingest: ${delivered.text.slice(0, 300)}`,
    ).toBeLessThan(400);

    // What the answer must NOT do is name a path or say whether a file was found. The caller is
    // anonymous, and an answer that varied with what is on disk would make this route a probe of it.
    expect(delivered.text).not.toContain(covePath);
    expect(delivered.text).not.toContain(tail);

    const registered = await pollUntil(
      () => videosIn(api),
      (videos) => videos.length > 0,
      {
        timeoutMs: IMPORT_BUDGET_MS,
        intervalMs: 1_000,
        label: "Cove to hold the video the delivery caused",
      },
    );

    expect(registered, "the delivery produced more than one item").toHaveLength(1);

    const held = await api.get(`/api/videos/${registered[0].id}`);
    expect(held.status, `GET the imported video answered: ${held.text.slice(0, 300)}`).toBe(200);
    expect(
      held.json.files?.map((file) => file.path),
      "the item Cove created is not at the path this extension verified",
    ).toEqual([covePath]);
  } finally {
    // Before the harness's own, which the isolated fixture runs after this test: the daemon refuses
    // to remove a network a container still holds an endpoint on.
    await whisparr.stop();
  }
});

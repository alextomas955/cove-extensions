// A redelivery naming a different file for a scene Cove already holds re-points the item the user
// already has, under both upgrade behaviours, and neither of them removes anything from disk.
//
// Every assertion is on the COVE side, read back through Cove's own video API, except the two that
// read the container's filesystem directly - which is the whole point of the second act: the
// superseded file has to still be there.
//
// The item's OWN file-count figure is not on Cove's video response, so what is asserted here is the
// item's file MEMBERSHIP. The host's recomputed FileCount column is asserted where it can be read, in
// the unit test that detaches over a real relational context.
import {
  expect,
  createApiClient,
  isolatedHarnessFixture,
  test as base,
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

// Transcribed by hand from the extension's own frozen constants.
const SECRET_HEADER = "X-Cove-Whisparr-Sync-Secret";
const SECRET_QUERY_PARAMETER = "s";

// Transcribed by hand from the delivery the pinned build made.
const V3_USER_AGENT = "Whisparr/3.3.8.1097 (alpine 3.23.5)";

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

/** The captured delivery, with only the file it names rewritten. */
function deliveryNaming(reportedPath, size) {
  const body = JSON.parse(readFileSync(CAPTURED_DELIVERY, "utf8"));
  body.movieFile.path = reportedPath;
  body.movieFile.size = size;
  return body;
}

async function configure(api, whisparr) {
  const saved = await api.put(SETTINGS_PATH, {
    selectedGeneration: "v3",
    v3: { address: whisparr.v3.internalBaseUrl, keyWrite: "replace", apiKey: whisparr.apiKey },
    v2: null,
  });
  expect(saved.status, `saving settings failed: ${saved.text.slice(0, 300)}`).toBe(200);
  return saved.json;
}

/** Stores one upgrade behaviour, naming neither generation, and answers with what was stored. */
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
 * briefly, and two reads a moment apart would otherwise be one answer - so a spec asserting that a
 * redelivery left something alone would be reading the value from before it.
 */
async function videoDetail(api, id) {
  const held = await api.get(`/api/videos/${id}?_=${randomUUID()}`);
  expect(held.status, `GET /api/videos/${id} answered: ${held.text.slice(0, 300)}`).toBe(200);
  return held.json;
}

test("a redelivery naming a different file re-points the item, and neither behaviour removes a file from disk", async ({
  isolatedHarness,
}) => {
  test.setTimeout(600_000);

  const api = createApiClient(
    () => isolatedHarness.baseUrl,
    () => isolatedHarness.token,
  );

  const whisparr = await startWhisparr({
    network: isolatedHarness.container.getNetworkNames()[0],
    generations: ["v3"],
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
    return { reportedPath: `${WHISPARR_ROOT}/${tail}`, covePath, size };
  }

  /** Whether the container still holds a file at `path`. */
  async function isOnDisk(path) {
    const { exitCode } = await isolatedHarness.exec(["test", "-f", path]);
    return exitCode === 0;
  }

  try {
    await registerRootFolder(whisparr.v3.container, whisparr.apiFor("v3"), "v3", WHISPARR_ROOT);
    const configured = await configure(api, whisparr);
    expect(
      configured?.upgradeBehavior,
      "the shipped default is not the one that leaves the superseded file attached",
    ).toBe("add");

    const secret = await callbackSecret(api);
    const asWhisparr = createApiClient(() => isolatedHarness.baseUrl, undefined, {
      headers: { [SECRET_HEADER]: secret, "User-Agent": V3_USER_AGENT },
    });

    const deliver = async (file, why) => {
      const answer = await asWhisparr.post(
        CALLBACK_PATH,
        deliveryNaming(file.reportedPath, file.size),
      );
      expect(answer.status, `${why}: ${answer.text.slice(0, 300)}`).toBeLessThan(400);
    };

    expect(await videosIn(api), "Cove already held a video before the first delivery").toEqual([]);

    // Act one: the scene arrives.
    const first = await place();
    await deliver(first, "the first delivery was refused, so nothing below is about the upgrade");

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
    const chosenTitle = `a title the user chose ${randomUUID()}`;
    const titled = await api.put(`/api/videos/${itemId}`, { title: chosenTitle });
    expect(titled.status, `setting the title answered: ${titled.text.slice(0, 300)}`).toBe(200);

    // Read back before the upgrade. Without it, a title the write never applied would be
    // indistinguishable from one the upgrade removed.
    expect(
      (await videoDetail(api, itemId)).title,
      "the title the user set never took, so the upgrade is not the subject",
    ).toBe(chosenTitle);

    // Act two: a better file for the SAME scene, under the default behaviour.
    const second = await place();
    await deliver(second, "the upgrade delivery was refused");

    // The wait ends on EITHER settled outcome, so the failure this act exists to catch - a second
    // item rather than a second file row - is reported as itself instead of as a poll timeout.
    const afterUpgrade = await pollUntil(
      async () => ({ item: await videoDetail(api, itemId), all: await videosIn(api) }),
      (seen) => (seen.item.files?.length ?? 0) > 1 || seen.all.length > 1,
      {
        timeoutMs: IMPORT_BUDGET_MS,
        intervalMs: 1_000,
        label: "the item to hold the file the upgrade named, or a second item to appear",
      },
    );
    expect(
      afterUpgrade.all.map((video) => video.id),
      "the upgrade created a second item instead of attaching the file to the one that exists",
    ).toEqual([itemId]);

    const upgraded = afterUpgrade.item;
    expect(
      upgraded.files?.map((file) => file.path).sort(),
      "the upgrade did not attach the new file to the item the user already had",
    ).toEqual([first.covePath, second.covePath].sort());
    expect(upgraded.title, "the upgrade overwrote the title the user set").toBe(chosenTitle);
    expect(await videosIn(api), "the upgrade created a second item").toHaveLength(1);

    // Act three: the other behaviour, chosen through the same settings route the page uses.
    expect(await chooseUpgradeBehavior(api, "replace")).toBe("replace");

    const third = await place();
    await deliver(third, "the second upgrade delivery was refused");

    const afterReplace = await pollUntil(
      async () => ({ item: await videoDetail(api, itemId), all: await videosIn(api) }),
      (seen) => seen.item.files?.length === 1 || seen.all.length > 1,
      {
        timeoutMs: IMPORT_BUDGET_MS,
        intervalMs: 1_000,
        label:
          "the item to hold only the file the second upgrade named, or a second item to appear",
      },
    );
    expect(
      afterReplace.all.map((video) => video.id),
      "the second upgrade created a second item instead of re-pointing the one that exists",
    ).toEqual([itemId]);

    const replaced = afterReplace.item;
    expect(
      replaced.files?.map((file) => file.path),
      "the item does not hold exactly the file the second upgrade named",
    ).toEqual([third.covePath]);
    expect(replaced.title, "the second upgrade overwrote the title the user set").toBe(chosenTitle);
    expect(await videosIn(api), "the second upgrade created a second item").toHaveLength(1);

    // The claim the whole second behaviour rests on: the rows left the item, the files did not leave
    // the disk. Asserted directly rather than inferred from the item no longer listing them.
    expect(await isOnDisk(first.covePath), "the detach removed the first file from disk").toBe(
      true,
    );
    expect(
      await isOnDisk(second.covePath),
      "the detach removed the superseded file from disk",
    ).toBe(true);
    expect(await isOnDisk(third.covePath), "the file the item kept is not on disk").toBe(true);
  } finally {
    await whisparr.stop();
  }
});

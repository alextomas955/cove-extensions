// A redelivery of a scene Cove already holds leaves a user's title and the item's one identity row
// alone, where Cove has no metadata source configured at all.
//
// That an imported item carries its identity at all is asserted on both generations by
// import-webhook.shared.spec.mjs. What is left here is the second delivery, which that scenario does
// not make: the first delivery below is the arrangement, and the reads taken between the two are
// what the redelivery is compared against.
//
// Every assertion is on the COVE side, read back through Cove's own video API. The callback's status
// is checked only so a refused delivery surfaces as itself rather than as a poll timeout.
//
// The harness Cove is configured with NO metadata source, which is Cove's own default. That is the
// deployment this spec exists for: the host's merge throws when the endpoint matches no configured
// source, so it cannot be what stamps the identity - the extension writes the row itself.
//
// A limit worth stating rather than leaving to be discovered: with no source configured, the
// resolution of the stamp's spelling falls back to the provider's standard address, which is the same
// answer the option's value alone would have given. So this spec does NOT discriminate between those
// two rules. What discriminates them is the unit test that configures a source at another spelling.
import {
  test as base,
  expect,
  createApiClient,
  isolatedHarnessFixture,
} from "@cove-extensions/e2e";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { placeVideoUnregistered } from "@cove-extensions/e2e/seed-media";
import { registerRootFolder, startWhisparr } from "@cove-extensions/e2e/whisparr";
import { randomUUID } from "node:crypto";
import { WHISPARR_SYNC_EXTENSION } from "../../lib/whisparr-sync-fixtures.mjs";
import {
  CALLBACK_ROUTE,
  COVE_ROOT,
  SECRET_HEADER,
  SETTINGS_ROUTE,
  USER_AGENT,
  WHISPARR_ROOT,
} from "../../lib/contract.mjs";
import { callbackSecret, deliveredRemoteId, deliveryNaming, videosIn } from "../../lib/steps.mjs";

// Transcribed by hand from the extension's own constant for the source v3 identifies against. This
// is what the stamp falls back to where the host is configured with no source, which it is here.
const STASHDB_ENDPOINT = "https://stashdb.org/graphql";

const IMPORT_BUDGET_MS = 120_000;

const test = base.extend({
  isolatedHarness: isolatedHarnessFixture(WHISPARR_SYNC_EXTENSION),
});

async function configure(api, whisparr) {
  const saved = await api.put(SETTINGS_ROUTE, {
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

/**
 * One video as Cove holds it, with its identity rows and its title.
 *
 * Each read carries its own query so it gets its own output-cache entry: the host caches this route
 * briefly, and two reads a moment apart would otherwise be one answer. A spec asserting that a write
 * took, or that a later delivery left it alone, would then be reading the value from before it.
 */
async function videoDetail(api, id) {
  const held = await api.get(`/api/videos/${id}?_=${randomUUID()}`);
  expect(held.status, `GET /api/videos/${id} answered: ${held.text.slice(0, 300)}`).toBe(200);
  return held.json;
}

/** Cove's own configured metadata sources, so "none configured" is measured and not assumed. */
async function configuredMetadataSources(api) {
  const config = await api.get("/api/system/config");
  expect(config.status, `GET /api/system/config answered: ${config.text.slice(0, 300)}`).toBe(200);
  return config.json?.scraping?.metadataServers ?? [];
}

test("a redelivery leaves a user's title and the item's one identity row alone, with no metadata source configured", async ({
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

  try {
    await registerRootFolder(whisparr.v3.container, whisparr.apiFor("v3"), "v3", WHISPARR_ROOT);
    await configure(api, whisparr);
    const secret = await callbackSecret(api);

    // The premise of the whole spec, measured off the host rather than assumed from its default.
    expect(
      await configuredMetadataSources(api),
      "this Cove has a metadata source configured, so it is not the deployment this spec is about",
    ).toEqual([]);

    const tail = `whisparr/${randomUUID()}.mp4`;
    const covePath = await placeVideoUnregistered({
      container: isolatedHarness.container,
      destPath: `${COVE_ROOT}/${tail}`,
    });

    // The negative first: no item, and so no identity row anywhere, before the delivery.
    const before = await videosIn(api);
    expect(
      before.map((video) => video.id),
      `Cove already held ${before.length} video(s) before the delivery`,
    ).toEqual([]);

    const { output } = await isolatedHarness.exec(["stat", "-c", "%s", covePath]);
    const size = Number(output.trim());
    expect(
      Number.isInteger(size) && size > 0,
      `stat reported ${output.trim()} for ${covePath}`,
    ).toBe(true);

    const asWhisparr = createApiClient(() => isolatedHarness.baseUrl, undefined, {
      headers: { [SECRET_HEADER]: secret, "User-Agent": USER_AGENT.v3 },
    });
    const body = deliveryNaming("v3", { path: `${WHISPARR_ROOT}/${tail}`, size: size });

    const delivered = await asWhisparr.post(CALLBACK_ROUTE, body);
    expect(
      delivered.status,
      `the callback refused the delivery, so nothing below is about the ingest: ${delivered.text.slice(0, 300)}`,
    ).toBeLessThan(400);

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

    const imported = await videoDetail(api, registered[0].id);

    // Read before the redelivery rather than asserted for its own sake: without it a second identity
    // row added by the redelivery would be indistinguishable from one the first delivery wrote.
    expect(
      imported.remoteIds,
      "the item arrived without its identity, so the redelivery below is not the subject",
    ).toEqual([{ endpoint: STASHDB_ENDPOINT, remoteId: deliveredRemoteId("v3") }]);

    // A title the user sets between the two deliveries. It has to survive the second, which it can
    // only do while the scene is never enriched twice.
    const chosenTitle = `a title the user chose ${randomUUID()}`;
    const titled = await api.put(`/api/videos/${imported.id}`, { title: chosenTitle });
    expect(titled.status, `setting the title answered: ${titled.text.slice(0, 300)}`).toBe(200);
    expect(titled.json?.title, `the write answered: ${titled.text.slice(0, 300)}`).toBe(
      chosenTitle,
    );

    // Read back before the redelivery. Without it, a title the write never applied would be
    // indistinguishable from one the redelivery removed.
    const chosen = await videoDetail(api, imported.id);
    expect(
      chosen.title,
      "the title the user set never took, so the redelivery is not the subject",
    ).toBe(chosenTitle);

    const redelivered = await asWhisparr.post(CALLBACK_ROUTE, body);
    expect(
      redelivered.status,
      `the redelivery was refused: ${redelivered.text.slice(0, 300)}`,
    ).toBeLessThan(400);

    // Nothing new to wait for, so the read has to be given a chance to be wrong: the ingest runs
    // inside the request, and this reads the state it left.
    const afterwards = await videoDetail(api, imported.id);
    expect(afterwards.title, "the redelivery overwrote the title the user set").toBe(chosenTitle);
    expect(
      afterwards.remoteIds,
      "the redelivery added a second identity row for one source",
    ).toEqual([{ endpoint: STASHDB_ENDPOINT, remoteId: deliveredRemoteId("v3") }]);
    expect(await videosIn(api), "the redelivery created a second item").toHaveLength(1);
  } finally {
    await whisparr.stop();
  }
});

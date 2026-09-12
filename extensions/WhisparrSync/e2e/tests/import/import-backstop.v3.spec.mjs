// A scene the library already holds, arriving again at a new path through the backstop alone.
//
// What a pass does - the first records where history ends and imports nothing, a later one imports
// what arrived after the mark - is asserted on both generations by import-backstop.shared.spec.mjs.
// The act here has no equivalent on the older generation: it turns on the library holding ONE item
// per scene rather than one per file, and that generation carries a scene as an episode of a site,
// so there is no second arrival at a new path to make.
//
// The passes below the last one are the arrangement rather than the subject. The claim is that the
// second arrival joins the item that exists rather than standing up a second one beside it. The wait
// ends on either settled outcome, so a duplicate reports as itself instead of as a poll timeout, and
// the file joining the item is what separates "did not duplicate" from "did nothing at all".
import { pollUntil } from "@cove-extensions/e2e/poll";
import { placeVideoUnregistered } from "@cove-extensions/e2e/seed-media";
import { randomUUID } from "node:crypto";

import { expect, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import { BACKSTOP_INTERVAL_FLOOR_SECONDS, COVE_ROOT, WHISPARR_ROOT } from "../../lib/contract.mjs";
import {
  readOptions,
  restartWorker,
  storedOptions,
  videoDetail,
  videoPathsIn,
  videosIn,
  writeOptions,
} from "../../lib/steps.mjs";

const SEEDED_ROWS = 3;

const WATERMARK_BUDGET_MS = 240_000;
const IMPORT_BUDGET_MS = 240_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

test("a scene the library already holds, arriving again at a new path, joins the item rather than doubling it", async ({
  isolatedCove,
  connected,
}) => {
  const { adapter, api, instance, whisparr } = connected;

  await whisparr.seedHistory("v3", { count: SEEDED_ROWS });
  const importedEventType = adapter.historyEventTypeId(whisparr);

  const configured = await storedOptions(api);
  await writeOptions(api, {
    ...configured,
    BackstopIntervalSeconds: BACKSTOP_INTERVAL_FLOOR_SECONDS,
  });
  await restartWorker(api);

  // The mark first, so the record seeded after it is one the walk reaches.
  await pollUntil(
    () => readOptions(api),
    (options) => Boolean(adapter.storedSection(options)?.BackstopWatermarkUtc),
    {
      timeoutMs: WATERMARK_BUDGET_MS,
      intervalMs: 2_000,
      label: "the first backstop pass to record where the instance's history ends",
    },
  );

  const first = `whisparr/${randomUUID()}.mp4`;
  const firstPath = await placeVideoUnregistered({
    container: isolatedCove.container,
    destPath: `${COVE_ROOT}/${first}`,
  });
  const firstRows = 1;
  await whisparr.seedHistory("v3", {
    count: firstRows,
    eventTypes: [importedEventType],
    data: [{ importedPath: `${WHISPARR_ROOT}/${first}` }],
    expectedTotal: SEEDED_ROWS + firstRows,
  });

  await pollUntil(
    () => videoPathsIn(api),
    (paths) => paths.includes(firstPath),
    {
      timeoutMs: IMPORT_BUDGET_MS,
      intervalMs: 2_000,
      label: "Cove to hold the file the first record named",
    },
  );

  // Both seeded records hang off the instance's one library entry, so every arrival on this channel
  // names the same scene. Without the identity on the item there is nothing for the second arrival
  // to match on, which would make the claim below pass for the wrong reason.
  const declaredIdentifier = await adapter.declaredSceneIdentifier(instance);
  const held = await videosIn(api);
  expect(
    held.map((video) => video.id),
    "the backstop stood up an item per file, so the arrival below has no single item to join",
  ).toHaveLength(1);

  const itemId = held[0].id;
  expect(
    (await videoDetail(api, itemId)).remoteIds,
    "the backstop registered the file without its identity, so nothing later can match on it",
  ).toEqual([{ endpoint: adapter.identityEndpoint, remoteId: declaredIdentifier }]);

  const upgrade = `whisparr/${randomUUID()}.mp4`;
  const upgradePath = await placeVideoUnregistered({
    container: isolatedCove.container,
    destPath: `${COVE_ROOT}/${upgrade}`,
  });
  const upgradeRows = 1;
  await whisparr.seedHistory("v3", {
    count: upgradeRows,
    eventTypes: [importedEventType],
    data: [{ importedPath: `${WHISPARR_ROOT}/${upgrade}` }],
    expectedTotal: SEEDED_ROWS + firstRows + upgradeRows,
  });

  // Either settled outcome ends the wait, so the failure this act exists to catch - a second item
  // rather than a second file row - is reported as itself instead of as a poll timeout.
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
    "a scene arriving through the backstop alone created a second item beside the one the library " +
      "already held",
  ).toEqual([itemId]);

  // What separates "did not duplicate" from "did nothing at all": had the pass not acted, the path
  // below would be on no item anywhere.
  expect(
    afterUpgrade.item.files?.map((file) => file.path),
    "the backstop did not attach the new file to the item the library already had",
  ).toContain(upgradePath);
  expect(
    afterUpgrade.item.remoteIds,
    "the re-point added a second identity row for one source",
  ).toEqual([{ endpoint: adapter.identityEndpoint, remoteId: declaredIdentifier }]);
});

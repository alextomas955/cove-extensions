// The backstop pass, against an instance that already HAS a past, on every generation that has one.
//
// One scenario, collected once per generation. The behaviour is one implementation; the reading
// underneath it is not. The history route goes through a different generated client per generation,
// and a record's identity is taken from a different member of a different shape, so neither
// execution says anything about the other.
//
// ACT ONE is the claim this scenario exists for: the first pass after connecting imports NOTHING.
// The past it runs against is one the backstop COULD import from - an import record naming a real
// file that really sits under a Cove root - because against a past nothing could be taken from,
// "imported nothing" and "could never import" are the same observation. BOTH readings are taken: the
// stored mark advances, and Cove's item count does not move.
//
// ACT TWO is what stops act one proving nothing. A later record names a SECOND file, and that file
// is expected to arrive, carrying the identity the instance itself declares for the scene. The
// record sitting exactly on the mark is read again by design, so the first file may arrive with it;
// what may not arrive is anything from further back.
//
// Across both acts the instance itself is read before and after: this channel reads and only reads,
// so its notification list and its own history record count must be exactly what this scenario put
// there.
import { tailContainerLog } from "@cove-extensions/e2e/harness";
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

// How many rows the fixture seeds before anything else, spanning every event type it declares.
const SEEDED_ROWS = 3;

// The whole past the instance holds when the extension is first pointed at it: the seeder's rows
// plus one import record naming a file that really exists under a Cove root. That last row is what
// makes "the first pass imported nothing" a claim a bug could fail.
const PAST_ROWS = SEEDED_ROWS + 1;

const WATERMARK_BUDGET_MS = 240_000;
const IMPORT_BUDGET_MS = 240_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    test.use({ generation });

    test("the first pass records where history ends and imports nothing, and a later record imports the file it names", async ({
      isolatedCove,
      connected,
    }) => {
      const { adapter, api, instance, whisparr } = connected;

      await whisparr.seedHistory(connected.generation, { count: SEEDED_ROWS });
      const importedEventType = adapter.historyEventTypeId(whisparr);

      // A past the backstop COULD import from. Without it "the first pass imported nothing" holds
      // for reasons that have nothing to do with the code, and the assertion is one no bug could
      // ever fail.
      const replayable = `whisparr/${randomUUID()}.mp4`;
      const replayablePath = await placeVideoUnregistered({
        container: isolatedCove.container,
        destPath: `${COVE_ROOT}/${replayable}`,
      });
      await whisparr.seedHistory(connected.generation, {
        count: 1,
        eventTypes: [importedEventType],
        data: [{ importedPath: `${WHISPARR_ROOT}/${replayable}` }],
        expectedTotal: PAST_ROWS,
      });

      // The interval has no control in the settings page, so it is written through Cove's own
      // extension-data route. Driving it to the floor is what makes the whole backstop path
      // reachable inside a container that cannot fake a clock.
      const configured = await storedOptions(api);
      expect(
        adapter.storedSection(configured)?.BackstopWatermarkUtc ?? null,
        "a mark was stored before any pass had run",
      ).toBeNull();
      await writeOptions(api, {
        ...configured,
        BackstopIntervalSeconds: BACKSTOP_INTERVAL_FLOOR_SECONDS,
      });

      // ACT ONE. The negative first: without it the reading after the pass could have been true all
      // along, and this scenario would pass with the whole walk deleted.
      const coveBefore = await videosIn(api);
      expect(
        coveBefore.map((video) => video.id),
        `Cove already held ${String(coveBefore.length)} video(s) before the first pass`,
      ).toEqual([]);

      // The instance as it stands before any pass has run. Taken here rather than at the start, so
      // what it measures is the backstop's effect and not the connection's.
      const before = await adapter.instanceState(instance);
      expect(before.historyRecords, "the instance was seeded with no past to speak of").toBe(
        PAST_ROWS,
      );

      await restartWorker(api);

      const afterFirstPass = await pollUntil(
        () => readOptions(api),
        (options) => Boolean(adapter.storedSection(options)?.BackstopWatermarkUtc),
        {
          timeoutMs: WATERMARK_BUDGET_MS,
          intervalMs: 2_000,
          label: "the first backstop pass to record where the instance's history ends",
        },
      );

      // That a mark is recorded at all is itself the evidence that the walk read THIS generation's
      // history: the route goes through a different generated client here, and one that answered
      // nothing would leave the mark unset.
      expect(
        Date.parse(adapter.storedSection(afterFirstPass).BackstopWatermarkUtc),
        "the recorded mark is not a readable instant",
      ).not.toBeNaN();
      expect(
        await videosIn(api),
        "the first pass after connecting imported something, which is a bulk replay",
      ).toEqual([]);

      // ACT TWO. A SECOND file, named by a record newer than the mark. Which of the two files
      // arrives is what tells a walk that stopped at the mark from one that read past it.
      const tail = `whisparr/${randomUUID()}.mp4`;
      const covePath = await placeVideoUnregistered({
        container: isolatedCove.container,
        destPath: `${COVE_ROOT}/${tail}`,
      });

      // One row, of the one kind, so it is the NEWEST row the instance holds. The seeder spaces rows
      // a minute apart from now, so a second kind would push the import row behind the mark act one
      // just recorded and the walk would rightly never reach it.
      const laterRows = 1;
      await whisparr.seedHistory(connected.generation, {
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
        const logged = await tailContainerLog(isolatedCove.container, { lines: 40 });
        // The exact page the walk reads. A record the pass should have acted on and a page it never
        // saw both arrive as "nothing was imported", and they are different failures.
        const walked = (await adapter.historyRows(instance)).map((record) => ({
          date: record.date,
          eventType: record.eventType,
          data: record.data,
        }));
        throw new Error(
          `${cause.message}\nThe extension recorded: mark ${adapter.storedSection(recorded)?.BackstopWatermarkUtc}, ` +
            `health ${JSON.stringify(recorded?.ImportHealth)}, ` +
            `refusals ${JSON.stringify(recorded?.ImportRefusals)}. ` +
            `The reported path was ${WHISPARR_ROOT}/${tail} and the file is at ${covePath}.\n` +
            `The page the walk reads holds:\n${JSON.stringify(walked, null, 2)}\n` +
            `The host's log tail:\n${logged}`,
        );
      });

      // Nothing beyond the two files a record named. The record sitting exactly ON the mark is read
      // again by design - the stop rule takes a record at the mark rather than skipping it - so the
      // first file may arrive on this pass too; anything else would be a walk reading past the mark.
      expect(
        registered.filter((path) => path !== covePath && path !== replayablePath),
        "the backstop imported a file no record it should have read ever named",
      ).toEqual([]);

      // The identity, read off a route this extension never calls, so what the stamp is checked
      // against comes from the instance rather than from the same answer the walk read it out of.
      const declaredIdentifier = await adapter.declaredSceneIdentifier(instance);
      const held = await videosIn(api);
      expect(
        held.map((video) => video.id),
        "the backstop stood up an item per file instead of one per scene",
      ).toHaveLength(1);
      expect(
        (await videoDetail(api, held[0].id)).remoteIds,
        "the imported item does not carry the identifier this generation's record named it by",
      ).toEqual([{ endpoint: adapter.identityEndpoint, remoteId: declaredIdentifier }]);

      // The backstop mutates the instance not at all. Its history holds exactly the rows this
      // scenario seeded, and its notification list is the one it had before any pass ran.
      const after = await adapter.instanceState(instance);
      expect(
        after.historyRecords,
        "the instance's history grew by something this scenario did not seed",
      ).toBe(PAST_ROWS + laterRows);
      expect(after.notifications, "a backstop pass changed the instance's notifications").toBe(
        before.notifications,
      );
    });
  });
}

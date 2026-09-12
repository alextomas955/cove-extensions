// A redelivery naming a different file for a scene Cove already holds re-points the item the user
// already has, under both upgrade behaviours, and neither of them removes anything from disk.
//
// One scenario, collected once per generation, each execution against an installation of its own.
// The upgrade decision is one implementation whichever instance delivered; what differs is the
// document the callback parses and the member the scene's identity sits on, and both are taken from
// the capture for the connected generation.
//
// Every assertion is on the COVE side, read back through Cove's own video API, except the two that
// read the container's filesystem directly - which is the whole point of the second act: the
// superseded file has to still be there.
//
// The item's OWN file-count figure is not on Cove's video response, so what is asserted here is the
// item's file MEMBERSHIP. The host's recomputed FileCount column is asserted where it can be read, in
// the unit test that detaches over a real relational context.
import { pollUntil } from "@cove-extensions/e2e/poll";
import { placeVideoUnregistered } from "@cove-extensions/e2e/seed-media";
import { randomUUID } from "node:crypto";

import { expect, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import { CALLBACK_ROUTE, COVE_ROOT, SETTINGS_ROUTE, WHISPARR_ROOT } from "../../lib/contract.mjs";
import {
  callbackSecret,
  deliveryNaming,
  videoDetail,
  videosIn,
  whisparrCaller,
} from "../../lib/steps.mjs";

const IMPORT_BUDGET_MS = 120_000;

/** The upgrade behaviour as the settings route reports it. */
async function storedUpgradeBehavior(api) {
  const read = await api.get(SETTINGS_ROUTE);
  expect(read.status, `GET ${SETTINGS_ROUTE} answered: ${read.text.slice(0, 300)}`).toBe(200);
  return read.json?.upgradeBehavior;
}

/** Stores one upgrade behaviour, leaving the stored credential alone, and answers with what took. */
async function chooseUpgradeBehavior(api, generation, behavior) {
  const saved = await api.put(SETTINGS_ROUTE, {
    selectedGeneration: generation,
    v3: null,
    v2: null,
    upgradeBehavior: behavior,
  });
  expect(saved.status, `saving the upgrade behaviour failed: ${saved.text.slice(0, 300)}`).toBe(
    200,
  );
  return saved.json?.upgradeBehavior;
}

test.describe.configure({ timeout: SPEC_BUDGET_MS });

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    test.use({ generation });

    test("a redelivery naming a different file re-points the item, and neither behaviour removes a file from disk", async ({
      isolatedCove,
      connected,
    }) => {
      const { api } = connected;

      /** Places one file under a Cove root and answers with its path and its size on disk. */
      async function place() {
        const tail = `whisparr/${randomUUID()}.mp4`;
        const covePath = await placeVideoUnregistered({
          container: isolatedCove.container,
          destPath: `${COVE_ROOT}/${tail}`,
        });
        const { output } = await isolatedCove.exec(["stat", "-c", "%s", covePath]);
        const size = Number(output.trim());
        expect(Number.isInteger(size) && size > 0, `stat reported ${output.trim()}`).toBe(true);
        return { reportedPath: `${WHISPARR_ROOT}/${tail}`, covePath, size };
      }

      /** Whether the container still holds a file at `path`. */
      async function isOnDisk(path) {
        const { exitCode } = await isolatedCove.exec(["test", "-f", path]);
        return exitCode === 0;
      }

      expect(
        await storedUpgradeBehavior(api),
        "the shipped default is not the one that leaves the superseded file attached",
      ).toBe("add");

      const secret = await callbackSecret(api);
      const asWhisparr = whisparrCaller(() => isolatedCove.baseUrl, connected.generation, {
        secret,
      });

      const deliver = async (file, why) => {
        const answer = await asWhisparr.post(
          CALLBACK_ROUTE,
          deliveryNaming(connected.generation, { path: file.reportedPath, size: file.size }),
        );
        expect(answer.status, `${why}: ${answer.text.slice(0, 300)}`).toBeLessThan(400);
      };

      expect(await videosIn(api), "Cove already held a video before the first delivery").toEqual(
        [],
      );

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
      const titled = await api.put(`/api/videos/${String(itemId)}`, { title: chosenTitle });
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
      expect(await chooseUpgradeBehavior(api, connected.generation, "replace")).toBe("replace");

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
      expect(replaced.title, "the second upgrade overwrote the title the user set").toBe(
        chosenTitle,
      );
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
    });
  });
}

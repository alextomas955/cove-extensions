// What the instance does after a library run: it takes in the file the run handed it and reports
// that import back. Cove must hold exactly what it held before.
//
// One scenario, collected once per generation, each execution against an installation of its own.
//
// WHY THIS IS NOT COVERED BY THE WEBHOOK SPECS. Those compose a delivery naming a file the
// instance downloaded, which is an arrival and must reach the library. This one names a file the
// LINKING half handed over, whose bytes the library already holds under the reader's own name. The
// two look alike on the wire and differ in one thing: whether the name is one this extension
// composed. A product that reads them alike doubles a reader's library with rows carrying no title
// and a file number for a name.
//
// WHY THE INSTANCE IS GIVEN THE LIBRARY ROOT. The extension only reads a delivery whose path lies
// under a root the reporting instance declares, and an installation where the instance downloads
// into the library declares that library root. Without it the delivery is refused for the root
// rather than settled for the name, and the spec would pass with the whole reading deleted.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the
// product. A red end-to-end run in this repository is usually the Cove container dying.
import { createApiClient } from "@cove-extensions/e2e";
import { registerRootFolder } from "@cove-extensions/e2e/whisparr";
import { pollUntil } from "@cove-extensions/e2e/poll";

import { expect, extensionRoute, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import { CALLBACK_ROUTE, SECRET_HEADER, SETTLE_DWELL_MS, USER_AGENT } from "../../lib/contract.mjs";
import { callbackSecret, deliveryNaming, videosIn } from "../../lib/steps.mjs";
import { namesIn, sharedBetweenBothProducts } from "../../lib/tree-steps.mjs";

/** The volume both containers reach, which the installation already declares as a library root. */
const COVE_ROOT = "/shared";

const RUN_BUDGET_MS = 240_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    test.use({ generation, ownedMedia: true });

    test("the instance reporting back a file the run handed it leaves the library as it was", async ({
      api,
      connected,
      isolatedCove,
    }) => {
      const { adapter, owned } = connected;
      const cove = isolatedCove.container;

      await registerRootFolder(
        connected.whisparr[generation].container,
        connected.instance,
        generation,
        COVE_ROOT,
      );
      await sharedBetweenBothProducts(cove, COVE_ROOT);

      const entityFolder = `${COVE_ROOT}/${adapter.treeFolder}/${owned.registeredAs}`;

      const started = await api.post(extensionRoute("sync/run"), { alsoMonitor: false });
      expect(
        started.json?.refusal ?? "none",
        `the run refused before it started: ${String(started.text).slice(0, 300)}`,
      ).toBe("none");
      const run = await pollUntil(
        async () =>
          (await api.get(extensionRoute(`job-status/${String(started.json?.jobId)}`))).json,
        (one) => /completed|failed|cancelled/i.test(String(one?.status)),
        { timeoutMs: RUN_BUDGET_MS, intervalMs: 2_000, label: "the library run's own job status" },
      );
      expect(run?.error ?? null, `the run faulted: ${String(run?.error)}`).toBeNull();
      expect(
        String(run?.status).toLowerCase(),
        `the run did not complete: ${JSON.stringify(run)}`,
      ).toBe("completed");

      // The subject of the delivery below. Without one the run linked nothing, and everything after
      // this would hold for a product that linked nothing either.
      const [linkName] = await namesIn(cove, entityFolder);
      expect(
        linkName,
        `the run linked nothing into ${entityFolder}, so there is no handover to report back: ${String(run?.summary)}`,
      ).toBeTruthy();
      const linkPath = `${entityFolder}/${linkName}`;

      // The file's real size, read off the file itself: the extension refuses a candidate whose
      // length disagrees with the delivery, and a made-up one would test that refusal instead.
      const { output } = await isolatedCove.exec(["stat", "-c", "%s", linkPath]);
      const size = Number(output.trim());
      expect(Number.isInteger(size) && size > 0, `stat reported ${output.trim()}`).toBe(true);

      const videosBefore = await videosIn(api);
      const atTheTopBefore = await namesIn(cove, COVE_ROOT);

      const asWhisparr = createApiClient(() => isolatedCove.baseUrl, undefined, {
        headers: {
          [SECRET_HEADER]: await callbackSecret(api),
          "User-Agent": USER_AGENT[generation],
        },
      });
      const delivered = await asWhisparr.post(
        CALLBACK_ROUTE,
        deliveryNaming(generation, { path: linkPath, size }),
      );
      expect(
        delivered.status,
        `the callback refused the delivery, so nothing below is about the reading: ${delivered.text.slice(0, 300)}`,
      ).toBeLessThan(400);

      // An absence read straight after the gesture is bounded by whatever delay the run happened to
      // have, so it is watched for a chosen window instead.
      await new Promise((settle) => setTimeout(settle, SETTLE_DWELL_MS));

      const videosAfter = await videosIn(api);
      expect(
        videosAfter.map((video) => video.id).sort(),
        `the delivery added ${String(videosAfter.length - videosBefore.length)} row(s) for a file the library already held`,
      ).toEqual(videosBefore.map((video) => video.id).sort());

      // The second NAME as well as the second row. A name at the top of the library root outlives
      // the row being deleted, and is what a reader is left tidying up.
      expect(
        await namesIn(cove, COVE_ROOT),
        "the delivery left a second name at the top of the library root",
      ).toEqual(atTheTopBefore);
    });
  });
}

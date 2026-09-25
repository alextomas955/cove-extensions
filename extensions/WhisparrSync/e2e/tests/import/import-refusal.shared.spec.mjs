// The two deliveries that are not the happy path, against the real host: a reported file present at
// no Cove root, and one present under two.
//
// One scenario, collected once per generation, each execution against an installation of its own.
// The resolution these refusals come out of runs the same lines whichever instance reported the
// path; what differs is the delivery document the callback parses, and that is taken from the
// capture for the connected generation.
//
// Both refusals are asserted on the COVE side - that no item was created, and what the extension's own
// stored aggregate says afterwards. The callback's status says the request was well formed and nothing
// about whether anything was registered, so it is a diagnostic here and never the subject.
//
// The third delivery is the control. Removing one of the two copies leaves exactly one candidate, and
// the same body then imports and clears that root's line: without it, a run in which the extension
// did nothing at all would satisfy every assertion above it.
import { pollUntil } from "@cove-extensions/e2e/poll";
import { addCoveLibraryRoot, placeVideoUnregistered } from "@cove-extensions/e2e/seed-media";
import { registerRootFolder } from "@cove-extensions/e2e/whisparr";
import { randomUUID } from "node:crypto";

import { expect, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import { CALLBACK_ROUTE, COVE_ROOT, WHISPARR_ROOT } from "../../lib/contract.mjs";
import {
  callbackSecret,
  deliveryNaming,
  importRefusals,
  refusalLineFor,
  videosIn,
  whisparrCaller,
} from "../../lib/steps.mjs";

// The wire spellings of why a path was refused, transcribed from the blob the backend serializes:
// camelCase, where the members around them are PascalCase.
const NOT_FOUND = "notFoundUnderAnyRoot";
const AMBIGUOUS = "ambiguousCandidates";

// A second root the instance declares for itself, and the Cove roots the harness declares. The
// Whisparr spelling names content Cove reaches by another name, which is the deployment this
// resolution exists for.
const WHISPARR_OTHER_ROOT = "/whisparr-elsewhere";
const COVE_ROOTS = ["/data", "/data2"];
const NESTED_COVE_ROOT = "/data/nested";

const REFUSAL_BUDGET_MS = 60_000;
const IMPORT_BUDGET_MS = 120_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    test.use({ generation });

    test("a reported file at no Cove root, and one at two, are each refused and counted", async ({
      isolatedCove,
      connected,
    }) => {
      const { api, instance, whisparr } = connected;

      // A second root beside the one the fixture's start registered, so a refusal under one root has
      // another root's line to be kept apart from.
      await registerRootFolder(
        whisparr[connected.generation].container,
        instance,
        connected.generation,
        WHISPARR_OTHER_ROOT,
      );

      const secret = await callbackSecret(api);

      const asWhisparr = whisparrCaller(() => isolatedCove.baseUrl, connected.generation, {
        secret,
      });

      /** Posts the captured body, naming `reportedPath`, as a real instance would. */
      const deliver = async (reportedPath, size) => {
        const delivered = await asWhisparr.post(
          CALLBACK_ROUTE,
          deliveryNaming(connected.generation, { path: reportedPath, size }),
        );
        // A diagnostic, not the evidence: a refused delivery surfacing as a poll timeout would name the
        // wrong cause entirely.
        expect(
          delivered.status,
          `the callback refused the delivery, so nothing below is about the ingest: ${delivered.text.slice(0, 300)}`,
        ).toBeLessThan(400);
      };

      // Nothing is stored and nothing is held, before any delivery. Without this the assertions below
      // could have been true all along.
      expect(await importRefusals(api), "the extension already held refusals").toEqual([]);
      expect(
        (await videosIn(api)).map((video) => video.id),
        "Cove already held a video before any delivery",
      ).toEqual([]);

      // ---- 1. a reported file present at no Cove root ----
      const absentTail = `${randomUUID()}.mp4`;
      await deliver(`${WHISPARR_OTHER_ROOT}/${absentTail}`, 4096);

      const notFound = await pollUntil(
        () => importRefusals(api),
        (refusals) => refusalLineFor(refusals, WHISPARR_OTHER_ROOT) !== undefined,
        {
          timeoutMs: REFUSAL_BUDGET_MS,
          intervalMs: 1_000,
          label: `a refusal counted against ${WHISPARR_OTHER_ROOT}`,
        },
      );

      const absent = refusalLineFor(notFound, WHISPARR_OTHER_ROOT);
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
        container: isolatedCove.container,
        destPath: `${COVE_ROOT}/${tail}`,
      });
      const inner = await placeVideoUnregistered({
        container: isolatedCove.container,
        destPath: `${NESTED_COVE_ROOT}/${tail}`,
      });
      const roots = await addCoveLibraryRoot(api, NESTED_COVE_ROOT, COVE_ROOTS);
      expect(
        roots,
        "Cove does not report the nested root that makes the delivery ambiguous",
      ).toContain(NESTED_COVE_ROOT);

      const { output } = await isolatedCove.exec(["stat", "-c", "%s", outer]);
      const size = Number(output.trim());
      expect(
        Number.isInteger(size) && size > 0,
        `stat reported ${output.trim()} for ${outer}`,
      ).toBe(true);

      expect(
        (await videosIn(api)).map((video) => video.id),
        "Cove already held a video before the ambiguous delivery",
      ).toEqual([]);

      await deliver(`${WHISPARR_ROOT}/${tail}`, size);

      const ambiguous = await pollUntil(
        () => importRefusals(api),
        (refusals) => refusalLineFor(refusals, WHISPARR_ROOT) !== undefined,
        {
          timeoutMs: REFUSAL_BUDGET_MS,
          intervalMs: 1_000,
          label: `a refusal counted against ${WHISPARR_ROOT}`,
        },
      );

      expect(
        refusalLineFor(ambiguous, WHISPARR_ROOT).NewestPaths.map((entry) => entry.Cause),
      ).toEqual([AMBIGUOUS]);
      expect(
        (await videosIn(api)).map((video) => video.id),
        "one of two candidates was imported rather than refused",
      ).toEqual([]);

      // The other root's line is untouched by a refusal under this one.
      expect(
        refusalLineFor(ambiguous, WHISPARR_OTHER_ROOT).NewestPaths.map((entry) => entry.Cause),
      ).toEqual([NOT_FOUND]);

      // ---- 3. the control: one copy removed, the same delivery imports ----
      await isolatedCove.exec(["rm", inner], { user: "root" });

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

      const held = await api.get(`/api/videos/${String(registered[0].id)}`);
      expect(held.status, `GET the imported video answered: ${held.text.slice(0, 300)}`).toBe(200);
      expect(
        held.json.files?.map((file) => file.path),
        "the item Cove created is not at the path this extension verified",
      ).toEqual([outer]);

      // That root's line is cleared by its own success, and the other root's survives it.
      const cleared = await pollUntil(
        () => importRefusals(api),
        (refusals) => refusalLineFor(refusals, WHISPARR_ROOT) === undefined,
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
    });
  });
}

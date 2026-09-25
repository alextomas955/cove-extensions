// The banner, on the settings page a user actually opens, against a live Cove and a live Whisparr.
//
// One scenario, collected once per generation, each execution against an installation of its own.
// Nothing below is a claim about one generation: the banner is raised by the resolution step, which
// runs the same lines whichever instance reported the path. What differs is the delivery document the
// callback parses, and that is taken from the capture for the connected generation.
//
// Four acts. The first asserts the banner is ABSENT before anything is delivered, so its later
// presence cannot have been true all along. The second builds one root's line up past the three paths
// it keeps. The third is the point of the whole design: a success under one root must clear that
// root's line and leave a root that is still failing exactly as it was. The fourth is the ordinary way
// a user recovers - they add the root they were missing, Cove's own import brings the file in, and the
// next delivery for it finds it already there - which must clear that root's line without waiting for
// a genuinely new file.
//
// Every assertion is on the Cove side - what the page draws, and what the extension stored. The
// callback's status says the request was well formed and nothing about whether anything was
// registered, so it is a diagnostic here and never the subject.
//
// The store is polled between deliveries and the PAGE is opened once per act: the banner reads once
// per page lifetime, so a fresh navigation is what re-reads it, and polling a navigation would pay a
// cold panel load per attempt.
import { pollUntil } from "@cove-extensions/e2e/poll";
import { placeVideoUnregistered, seedVideo } from "@cove-extensions/e2e/seed-media";
import { registerRootFolder } from "@cove-extensions/e2e/whisparr";
import { randomUUID } from "node:crypto";

import { expect, test } from "../../lib/connected-fixture.mjs";
import {
  CALLBACK_ROUTE,
  COVE_ROOT,
  EXTENSION_ID,
  SETTINGS_PAGE_PATH,
  WHISPARR_ROOT,
} from "../../lib/contract.mjs";
import {
  callbackSecret,
  deliveryNaming,
  importRefusals,
  refusalLineFor,
  videosIn,
  whisparrCaller,
} from "../../lib/steps.mjs";

// Wider than the fixture's own budget: nine deliveries and seven panel loads sit on top of the
// container stack this scenario starts.
const BANNER_BUDGET_MS = 1_200_000;

// What the panel's own banner read asks for. Matched as a tail rather than a whole address, because
// the page reaches it through the host's own client and the origin is the browser's.
const BANNER_ROUTE_TAIL = `/api/extensions/${EXTENSION_ID}/import/banner`;

// Transcribed by hand from the bundle's own copy module. A fragment rather than the whole sentence:
// the sentence carries a character this file would otherwise have to reproduce exactly to find it.
const BANNER_HEADING = "Cove can't find imported files";

// A second root the instance declares for itself, beside the one the fixture's start declares. The
// Whisparr spellings name content Cove reaches by another name, which is the deployment this surface
// exists for.
const WHISPARR_OTHER_ROOT = "/whisparr-elsewhere";

// Named per operation, so a failure says which act blew its budget rather than which line.
const ACT_TWO_REFUSAL_BUDGET_MS = 60_000;
const ACT_THREE_REFUSAL_BUDGET_MS = 60_000;
const ACT_THREE_IMPORT_BUDGET_MS = 120_000;
const ACT_FOUR_REFUSAL_BUDGET_MS = 60_000;
const ACT_FOUR_CLEAR_BUDGET_MS = 60_000;

// A cold container serving the extension bundle for the first time is slow rather than broken and
// raises no signal to wait on.
const PANEL_ATTEMPT_BUDGET_MS = 60_000;
const PANEL_ATTEMPTS = 3;

// Between the banner's answer landing and the render it causes. A frame, not a container: the wait
// this covers is React committing, which every other wait in this file is far too long for.
const RENDER_SETTLE_MS = 500;

/**
 * Opens the settings panel and waits for this extension's own component to render.
 *
 * The path is not one of the host's own routes. The host carries the unknown key only until it
 * finishes loading extensions, then answers a load that produced no matching tab by switching to its
 * first built-in tab and rewriting the address. Nothing after that rewrite can reach this panel, and
 * only a fresh navigation recovers it.
 *
 * The card the panel opens on is the connected generation's, because that is the one the stored
 * settings name, so the address field below resolves to one control rather than two.
 */
async function openPanel(page, baseUrl) {
  const addressField = page.getByPlaceholder("http://whisparr:6969");
  let answered = false;
  for (let attempt = 1; attempt <= PANEL_ATTEMPTS; attempt++) {
    // Registered BEFORE the navigation. The banner's answer can land before the panel's own field is
    // visible, and a wait started afterwards would never see it - which would let an absent banner
    // stand for a read that never ran.
    const bannerRead = page
      .waitForResponse((response) => response.url().includes(BANNER_ROUTE_TAIL), {
        timeout: PANEL_ATTEMPT_BUDGET_MS,
      })
      .then(() => true)
      .catch(() => false);

    await page.goto(`${baseUrl}${SETTINGS_PAGE_PATH}`);
    const rendered = await addressField
      .waitFor({ state: "visible", timeout: PANEL_ATTEMPT_BUDGET_MS })
      .then(() => true)
      .catch(() => false);
    answered = await bannerRead;
    if (rendered && answered) break;
  }

  await expect(
    addressField,
    `the panel never rendered at ${baseUrl}${SETTINGS_PAGE_PATH}; the page is now at ${page.url()}`,
  ).toBeVisible();
  expect(
    answered,
    `the panel never asked for the banner at ${baseUrl}${SETTINGS_PAGE_PATH}, so nothing it does or does not show is evidence`,
  ).toBe(true);

  // The answer has landed; this covers the render it causes, which is a frame rather than a
  // container.
  await page.waitForTimeout(RENDER_SETTLE_MS);
}

/**
 * The banner as it stands on the open panel: null when there is none, otherwise one entry per root
 * line in the order the block draws them.
 */
async function bannerOn(page) {
  const block = page.getByRole("alert").filter({ hasText: BANNER_HEADING });
  if ((await block.count()) === 0) {
    return null;
  }
  return block.first().locator(":scope > ul > li").allInnerTexts();
}

/** Opens the panel and reads the banner off it. */
async function bannerAfterOpening(page, baseUrl) {
  await openPanel(page, baseUrl);
  return bannerOn(page);
}

test.describe.configure({ timeout: BANNER_BUDGET_MS });

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    test.use({ generation });

    test("the banner names each failing root, bounds its list, and clears only the root that worked", async ({
      isolatedCove,
      baseUrl,
      connected,
      page,
    }) => {
      const { api, instance, whisparr } = connected;

      // A second root beside the one the fixture's start registered, so a success under one root has
      // another root's line to leave alone.
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
          `the callback refused the delivery, so nothing below is about the banner: ${delivered.text.slice(0, 300)}`,
        ).toBeLessThan(400);
      };

      // ---- act one: nothing delivered, nothing shown ----
      expect(await importRefusals(api), "the extension already held refusals").toEqual([]);
      expect(
        await bannerAfterOpening(page, baseUrl),
        "the banner was on the page before anything had been delivered",
      ).toBeNull();

      // ---- act two: one root, four refusals, three paths ----
      const tails = Array.from({ length: 4 }, () => `${randomUUID()}.mp4`);
      await deliver(`${WHISPARR_ROOT}/${tails[0]}`, 4096);

      await pollUntil(
        () => importRefusals(api),
        (refusals) => refusalLineFor(refusals, WHISPARR_ROOT)?.CountSinceLastSuccess === 1,
        {
          timeoutMs: ACT_TWO_REFUSAL_BUDGET_MS,
          intervalMs: 1_000,
          label: `act two: the first refusal counted against ${WHISPARR_ROOT}`,
        },
      );

      const first = await bannerAfterOpening(page, baseUrl);
      expect(first, "act two: the banner did not appear after the first refusal").not.toBeNull();
      expect(first).toHaveLength(1);
      expect(first[0]).toContain(WHISPARR_ROOT);
      expect(first[0]).toContain(tails[0]);
      expect(first[0], "act two: the listed path did not name why it was refused").toContain(
        "No Cove library folder holds this file.",
      );

      for (const [index, tail] of tails.slice(1).entries()) {
        await deliver(`${WHISPARR_ROOT}/${tail}`, 4096);
        await pollUntil(
          () => importRefusals(api),
          (refusals) =>
            refusalLineFor(refusals, WHISPARR_ROOT)?.CountSinceLastSuccess === index + 2,
          {
            timeoutMs: ACT_TWO_REFUSAL_BUDGET_MS,
            intervalMs: 1_000,
            label: `act two: refusal ${index + 2} counted against ${WHISPARR_ROOT}`,
          },
        );
      }

      const bounded = await bannerAfterOpening(page, baseUrl);
      expect(
        bounded,
        "act two: the banner went away while refusals were outstanding",
      ).not.toBeNull();
      expect(bounded).toHaveLength(1);

      // Four refusals, three paths, and the one no longer listed is the oldest.
      expect(bounded[0], "act two: the count is not the stored one").toContain("4");
      for (const tail of tails.slice(1)) {
        expect(bounded[0], `act two: ${tail} is not listed`).toContain(tail);
      }
      expect(
        bounded[0],
        "act two: the oldest path is still listed, so the list is not bounded at three",
      ).not.toContain(tails[0]);

      // ---- act three: a second root fails, the first one starts working ----
      const otherTail = `${randomUUID()}.mp4`;
      await deliver(`${WHISPARR_OTHER_ROOT}/${otherTail}`, 4096);

      await pollUntil(
        () => importRefusals(api),
        (refusals) => refusalLineFor(refusals, WHISPARR_OTHER_ROOT) !== undefined,
        {
          timeoutMs: ACT_THREE_REFUSAL_BUDGET_MS,
          intervalMs: 1_000,
          label: `act three: a refusal counted against ${WHISPARR_OTHER_ROOT}`,
        },
      );

      const bothFailing = await bannerAfterOpening(page, baseUrl);
      expect(bothFailing, "act three: the banner went away with two roots failing").not.toBeNull();
      expect(bothFailing).toHaveLength(2);

      const otherLineBefore = bothFailing.find((line) => line.includes(WHISPARR_OTHER_ROOT));
      expect(otherLineBefore, `act three: no line named ${WHISPARR_OTHER_ROOT}`).toBeDefined();

      // A file that really resolves, from the FIRST root. Placed but not registered, so the item Cove
      // ends up holding is one this extension caused.
      const resolvingTail = `${randomUUID()}.mp4`;
      const placed = await placeVideoUnregistered({
        container: isolatedCove.container,
        destPath: `${COVE_ROOT}/${resolvingTail}`,
      });
      const { output } = await isolatedCove.exec(["stat", "-c", "%s", placed]);
      const size = Number(output.trim());
      expect(
        Number.isInteger(size) && size > 0,
        `stat reported ${output.trim()} for ${placed}`,
      ).toBe(true);

      expect(
        (await videosIn(api)).map((video) => video.id),
        "act three: Cove already held a video before the resolving delivery",
      ).toEqual([]);

      await deliver(`${WHISPARR_ROOT}/${resolvingTail}`, size);

      const registered = await pollUntil(
        () => videosIn(api),
        (videos) => videos.length > 0,
        {
          timeoutMs: ACT_THREE_IMPORT_BUDGET_MS,
          intervalMs: 1_000,
          label: "act three: Cove to hold the video the resolving delivery caused",
        },
      );
      expect(registered, "act three: the delivery produced more than one item").toHaveLength(1);

      await pollUntil(
        () => importRefusals(api),
        (refusals) => refusalLineFor(refusals, WHISPARR_ROOT) === undefined,
        {
          timeoutMs: ACT_THREE_REFUSAL_BUDGET_MS,
          intervalMs: 1_000,
          label: `act three: ${WHISPARR_ROOT}'s line to be cleared by its own success`,
        },
      );

      const afterOneSucceeded = await bannerAfterOpening(page, baseUrl);
      expect(
        afterOneSucceeded,
        "act three: one root's success took the whole banner away while another root was still failing",
      ).not.toBeNull();
      expect(afterOneSucceeded).toHaveLength(1);
      expect(
        afterOneSucceeded[0],
        `act three: ${WHISPARR_ROOT}'s line survived its own success`,
      ).not.toContain(WHISPARR_ROOT);
      expect(
        afterOneSucceeded[0],
        `act three: ${WHISPARR_OTHER_ROOT}'s line changed although nothing happened under it`,
      ).toBe(otherLineBefore);

      // ---- the control: the second root works too, and the banner goes away entirely ----
      const lastTail = `${randomUUID()}.mp4`;
      const lastPlaced = await placeVideoUnregistered({
        container: isolatedCove.container,
        destPath: `${COVE_ROOT}/${lastTail}`,
      });
      const last = await isolatedCove.exec(["stat", "-c", "%s", lastPlaced]);
      const lastSize = Number(last.output.trim());
      expect(
        Number.isInteger(lastSize) && lastSize > 0,
        `stat reported ${last.output.trim()}`,
      ).toBe(true);

      await deliver(`${WHISPARR_OTHER_ROOT}/${lastTail}`, lastSize);

      await pollUntil(
        () => importRefusals(api),
        (refusals) => refusals.length === 0,
        {
          timeoutMs: ACT_THREE_IMPORT_BUDGET_MS,
          intervalMs: 1_000,
          label: `act three: ${WHISPARR_OTHER_ROOT}'s line to be cleared by its own success`,
        },
      );

      expect(
        await bannerAfterOpening(page, baseUrl),
        "act three: the banner is still on the page with nothing left to report",
      ).toBeNull();

      // ---- act four: the user fixes the root, Cove's own import brings the file in ----
      // Two lines raised first, so the clear below has something to clear and something to leave alone.
      const recoveredTail = `${randomUUID()}.mp4`;
      const stillFailingTail = `${randomUUID()}.mp4`;
      await deliver(`${WHISPARR_ROOT}/${recoveredTail}`, 4096);
      await deliver(`${WHISPARR_OTHER_ROOT}/${stillFailingTail}`, 4096);

      await pollUntil(
        () => importRefusals(api),
        (refusals) =>
          refusalLineFor(refusals, WHISPARR_ROOT) !== undefined &&
          refusalLineFor(refusals, WHISPARR_OTHER_ROOT) !== undefined,
        {
          timeoutMs: ACT_FOUR_REFUSAL_BUDGET_MS,
          intervalMs: 1_000,
          label: "act four: a refusal counted against each root",
        },
      );

      const stillFailingBefore = (await bannerAfterOpening(page, baseUrl))?.find((line) =>
        line.includes(WHISPARR_OTHER_ROOT),
      );
      expect(stillFailingBefore, `act four: no line named ${WHISPARR_OTHER_ROOT}`).toBeDefined();

      const idsBeforeSeed = (await videosIn(api)).map((video) => video.id);

      // Registered through COVE's own import route rather than this extension's, which is what the user
      // gets when they add the missing root and let the library scan run. The item therefore exists
      // without this extension having caused it.
      await seedVideo({
        container: isolatedCove.container,
        baseUrl: isolatedCove.baseUrl,
        token: isolatedCove.token,
        destName: recoveredTail,
      });

      // Polled, not assumed: the redelivery below only exercises the already-held branch once Cove
      // itself holds the file, so this is the precondition of the act rather than a convenience.
      const idsAfterSeed = await pollUntil(
        () => videosIn(api).then((videos) => videos.map((video) => video.id)),
        (ids) => ids.length > idsBeforeSeed.length,
        {
          timeoutMs: ACT_FOUR_CLEAR_BUDGET_MS,
          intervalMs: 1_000,
          label: `act four: Cove's own import to hold ${recoveredTail} (was ${JSON.stringify(idsBeforeSeed)})`,
        },
      );

      const recovered = await isolatedCove.exec([
        "stat",
        "-c",
        "%s",
        `${COVE_ROOT}/${recoveredTail}`,
      ]);
      const recoveredSize = Number(recovered.output.trim());
      expect(
        Number.isInteger(recoveredSize) && recoveredSize > 0,
        `stat reported ${recovered.output.trim()}`,
      ).toBe(true);

      // The same reported path as the refused delivery. Only the size differs, and the size decides
      // nothing until there is a file to compare it against.
      await deliver(`${WHISPARR_ROOT}/${recoveredTail}`, recoveredSize);

      await pollUntil(
        () => importRefusals(api),
        (refusals) => refusalLineFor(refusals, WHISPARR_ROOT) === undefined,
        {
          timeoutMs: ACT_FOUR_CLEAR_BUDGET_MS,
          intervalMs: 1_000,
          label: `act four: ${WHISPARR_ROOT}'s line to be cleared by a delivery it already held`,
        },
      );

      expect(
        refusalLineFor(await importRefusals(api), WHISPARR_OTHER_ROOT),
        `act four: ${WHISPARR_OTHER_ROOT}'s line went with it, so the clear was not per root`,
      ).toBeDefined();

      // The same items, not merely the same number of them: a redelivery for a file the library already
      // holds must neither stand up a second item nor take one away.
      expect(
        (await videosIn(api)).map((video) => video.id),
        "act four: the redelivery changed which items Cove holds for a file it already had",
      ).toEqual(idsAfterSeed);

      const afterRecovery = await bannerAfterOpening(page, baseUrl);
      expect(afterRecovery, "act four: the whole banner went away").not.toBeNull();
      expect(afterRecovery).toHaveLength(1);
      expect(
        afterRecovery[0],
        `act four: ${WHISPARR_ROOT}'s line survived a delivery whose file the library already held`,
      ).not.toContain(WHISPARR_ROOT);
      expect(
        afterRecovery[0],
        `act four: ${WHISPARR_OTHER_ROOT}'s line changed although nothing happened under it`,
      ).toBe(stillFailingBefore);
    });
  });
}

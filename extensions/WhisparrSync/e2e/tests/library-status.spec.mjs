// What a studio card says about the connected instance, in a real containerized host.
//
// WHY THIS SPEC EXISTS. Three strings bind the toolbar control and the card badge across two
// repositories: the host's slot name, the manifest's component name, and the key the bundle
// registers. The host resolves each by exact string and renders nothing, with no error anywhere,
// when any pair differs. Nothing below the browser can see that, so a control that silently never
// appears is only reported here.
//
// WHAT IT NEEDS. A Cove container, an installed extension and a real Whisparr instance. No metadata
// credential: the identifier each card is named by is the library's own stored row, and this surface
// reaches no provider at all.
//
// WHY THE TITLE MATTERS. Playwright's --grep matches the concatenated title and never the filename,
// so the describe title below is what selects this file's tests. Every test added here goes inside
// the same block.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { registerRootFolder, startWhisparr } from "@cove-extensions/e2e/whisparr";
import { randomUUID } from "node:crypto";

import {
  test as base,
  connectWhisparr,
  expect,
  seedCoveStudio,
  SETTLE_DWELL_MS,
  STASHDB_ENDPOINT,
  WHISPARR_ROOT,
  WHISPARR_SYNC_EXTENSION,
} from "../lib/whisparr-sync-fixtures.mjs";

/** The control's two names, transcribed by hand from the shipped sentences. */
const SHOW_STATUS = "Show Whisparr status";

/**
 * The five words a badge may read, transcribed by hand from the shipped vocabulary.
 *
 * A spec importing the constants the product declares would be asserting that a string equals
 * itself.
 */
const STATE_WORDS = ["Monitored", "Unmonitored", "Not added", "Excluded", "Status unknown"];

/** A real StashDB studio id. The library stores it, and the extension names the instance by it. */
const BRAZZERS_EXXTRA = "39cee498-a9ac-4403-910a-1a0157ad22d8";

const BUNDLE_BUDGET_MS = 60_000;
const BUNDLE_ATTEMPTS = 3;
const GRID_BUDGET_MS = 60_000;
const BADGE_BUDGET_MS = 90_000;

const test = base.extend({
  libraryHarness: [
    async ({}, use) => {
      const harness = await startHarness();
      try {
        harness.owner = await harness.bootstrapOwner();
        await harness.installExtension(WHISPARR_SYNC_EXTENSION);
        await use(harness);
      } finally {
        await harness.stop();
      }
    },
    { scope: "test" },
  ],

  // Read through the handle AFTER the install. The install restarts the container, which re-mints
  // the token and can republish the instance on a different host port.
  baseUrl: async ({ libraryHarness }, use) => {
    await use(libraryHarness.baseUrl);
  },
});

/** The control, by the only name it has. Its accessible name carries a reason when there is one. */
const statusToggle = (page) => page.getByRole("button", { name: new RegExp(`^${SHOW_STATUS}`) });

/** Every card the studios grid drew. */
const studioCards = (page) => page.locator(".entity-card");

/** One studio's own card, found by the name it was seeded under. */
const cardFor = (page, name) => studioCards(page).filter({ hasText: name });

/**
 * Every state chip inside a card's own extension box.
 *
 * The chip's label shares its element with an aria-hidden glyph, so the element carries both and an
 * exact-text locator finds nothing. The shape is located here and the words are asserted off it.
 */
const stateChips = (scope) => scope.locator(".card-extension span.rounded-full");

/** The words a chip may read, as the element carries them: a glyph, then the label. */
const STATE_CHIP_TEXT = new RegExp(`(${STATE_WORDS.join("|")})$`);

/**
 * Opens `path`, re-navigating while nothing the caller named has rendered.
 *
 * The host paints its own error boundary in place of a page whose lazily-imported chunk failed to
 * fetch, on the correct URL and indefinitely. Only a fresh navigation recovers it, and the retry is
 * bounded so a permanent failure is not turned into a hung test.
 */
async function visit(page, baseUrl, path, present, label) {
  for (let attempt = 1; attempt <= BUNDLE_ATTEMPTS; attempt++) {
    await page.goto(`${baseUrl}${path}`);
    const rendered = await present
      .waitFor({ state: "visible", timeout: BUNDLE_BUDGET_MS })
      .then(() => true)
      .catch(() => false);
    if (rendered) return;
  }
  throw new Error(
    `${label}: nothing rendered at ${baseUrl}${path} across ${BUNDLE_ATTEMPTS} navigation(s) of ${BUNDLE_BUDGET_MS}ms each; the page is now at ${page.url()}`,
  );
}

test.describe("library status", () => {
  test("studio status behind the pill", async ({ page, baseUrl, libraryHarness }) => {
    // A container pair, an extension install, a browser and a real instance. Well above the shared
    // per-test budget, and deliberately its own number rather than a raised default for every spec.
    test.setTimeout(900_000);

    const coveApi = createApiClient(
      () => libraryHarness.baseUrl,
      () => libraryHarness.token,
    );

    // Everything the browser reported, so a bundle-load throw is named by this spec rather than left
    // as a card that simply drew nothing.
    const consoleErrors = [];
    page.on("console", (message) => {
      if (message.type() === "error") consoleErrors.push(message.text());
    });
    page.on("pageerror", (failure) => {
      consoleErrors.push(String(failure));
    });

    const whisparr = await startWhisparr({
      network: libraryHarness.container.getNetworkNames()[0],
      generations: ["v3"],
    });

    try {
      const instance = whisparr.apiFor("v3");
      whisparr.v3.rootFolder = await registerRootFolder(
        whisparr.v3.container,
        instance,
        "v3",
        WHISPARR_ROOT,
      );
      await connectWhisparr(coveApi, whisparr, "v3");

      const identified = await seedCoveStudio(coveApi, {
        name: `Identified ${randomUUID().slice(0, 8)}`,
        remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: BRAZZERS_EXXTRA }],
      });
      const unidentified = await seedCoveStudio(coveApi, {
        name: `Unidentified ${randomUUID().slice(0, 8)}`,
        remoteIds: [],
      });

      await visit(page, baseUrl, "/studios", studioCards(page).first(), "the studios page");
      await expect(
        cardFor(page, identified.name),
        "the studios grid drew no card for the seeded studio",
      ).toBeVisible({ timeout: GRID_BUDGET_MS });

      // Off by default. A card with the control off is what a card with no extension registered
      // looks like, so nothing in the vocabulary may be on the page yet.
      await expect(statusToggle(page), "the toolbar drew no Whisparr status control").toBeVisible();
      await expect(
        stateChips(page),
        "a state chip was on screen before the control was pressed",
      ).toHaveCount(0);

      await statusToggle(page).click();

      const chip = stateChips(cardFor(page, identified.name));
      await expect(
        chip,
        "the identified studio's card carries no state chip, or carries more than one",
      ).toHaveCount(1, { timeout: BADGE_BUDGET_MS });
      await expect(
        chip,
        "the chip reads something outside this product's own five-state vocabulary",
      ).toHaveText(STATE_CHIP_TEXT);

      // An absence watched for a fixed dwell. A chip that has not rendered yet is otherwise
      // indistinguishable from one that never will.
      await page.waitForTimeout(SETTLE_DWELL_MS);
      await expect(
        stateChips(cardFor(page, unidentified.name)),
        "a studio Cove holds no usable link for was given a state anyway",
      ).toHaveCount(0);

      const missingComponent = consoleErrors.filter((line) =>
        /component not found|does not provide an export|SyntaxError/i.test(line),
      );
      expect(
        missingComponent,
        `a page reported a component the bundle does not register: ${missingComponent.join(" | ")}`,
      ).toEqual([]);
    } finally {
      await whisparr.stop();
    }
  });
});

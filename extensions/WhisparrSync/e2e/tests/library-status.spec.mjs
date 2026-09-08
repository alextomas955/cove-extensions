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
  extensionRoute,
  seedCoveStudio,
  SETTLE_DWELL_MS,
  STASHDB_ENDPOINT,
  WHISPARR_ROOT,
  WHISPARR_SYNC_EXTENSION,
} from "../lib/whisparr-sync-fixtures.mjs";

/** The control's two names, transcribed by hand from the shipped sentences. */
const SHOW_STATUS = "Show Whisparr status";
const HIDE_STATUS = "Hide Whisparr status";

/**
 * The reason the control states for the whole page when nothing answered, transcribed the same way.
 *
 * The second sentence is the one this measurement is about: a page of cards that simply drew no
 * badge would read as a library Whisparr holds nothing for.
 */
const COULD_NOT_BE_READ =
  "Cove could not reach Whisparr, so no card can show a status. That is not the same as Whisparr holding nothing.";

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

/** Long enough for the control's own colour transition to finish. */
const COLOUR_SETTLE_MS = 10_000;
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

/**
 * The control, by the only name it has, in either state.
 *
 * Its accessible name carries a reason after it when there is one, so the match is anchored at the
 * front rather than exact.
 */
const statusToggle = (page) =>
  page.getByRole("button", { name: new RegExp(`^(${SHOW_STATUS}|${HIDE_STATUS})`) });

/** The badge's own strip, which is the element the host clips. */
const badgeStrip = (scope) => scope.locator(".card-extension > div");

/** The route one press asks, as the host mounts it. */
const STATUS_ROUTE = extensionRoute("library/studio/status");

/**
 * The metadata sources this product reads a catalogue from.
 *
 * Named here and nowhere in the shipped bundle: which source answers follows the connected
 * generation, so a name written into the product would be wrong on the other one.
 */
const PROVIDER_HOSTS = ["stashdb.org", "theporndb.net"];

/** Every request the page made, so a claim about what a press costs is read off the network. */
function watchRequests(page) {
  const seen = [];
  page.on("request", (request) => {
    seen.push({ url: request.url(), method: request.method() });
  });
  return {
    toStatusRoute: () => seen.filter((request) => request.url.includes(STATUS_ROUTE)),
    toProviders: () =>
      seen.filter((request) => PROVIDER_HOSTS.some((host) => request.url.includes(host))),
  };
}

/** Points the extension at an address nothing answers, with no instance started at all. */
async function connectNothing(api) {
  const saved = await api.put(extensionRoute("settings"), {
    selectedGeneration: "v3",
    v3: {
      address: "http://127.0.0.1:6969",
      keyWrite: "replace",
      apiKey: "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e",
    },
    v2: null,
  });
  if (saved.status !== 200) {
    throw new Error(`connectNothing: PUT settings answered ${saved.status}`);
  }
}

/** What the extension's own entity route says this studio is, read through the host. */
async function readMonitoring(api, coveId) {
  const answered = await api.get(extensionRoute(`entity/studio/${String(coveId)}/monitoring`));
  expect(answered.status, `GET the studio's monitoring answered ${String(answered.status)}`).toBe(
    200,
  );
  return { present: answered.json.present, monitored: answered.json.monitored };
}

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

/** The Cove api client for one started harness. */
function apiFor(harness) {
  return createApiClient(
    () => harness.baseUrl,
    () => harness.token,
  );
}

/** One studio the connected instance can be asked about, and one it cannot. */
async function seedTwoStudios(api) {
  return {
    identified: await seedCoveStudio(api, {
      name: `Identified ${randomUUID().slice(0, 8)}`,
      remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: BRAZZERS_EXXTRA }],
    }),
    unidentified: await seedCoveStudio(api, {
      name: `Unidentified ${randomUUID().slice(0, 8)}`,
      remoteIds: [],
    }),
  };
}

/** Starts one instance, points the extension at it, runs `body`, and stops it either way. */
async function usingInstance(harness, api, body) {
  const whisparr = await startWhisparr({
    network: harness.container.getNetworkNames()[0],
    generations: ["v3"],
  });
  try {
    whisparr.v3.rootFolder = await registerRootFolder(
      whisparr.v3.container,
      whisparr.apiFor("v3"),
      "v3",
      WHISPARR_ROOT,
    );
    await connectWhisparr(api, whisparr, "v3");
    await body(whisparr);
  } finally {
    await whisparr.stop();
  }
}

/** Opens the studios grid and waits for the cards and the control to be on screen. */
async function openStudios(page, baseUrl) {
  await visit(page, baseUrl, "/studios", studioCards(page).first(), "the studios page");
  await expect(statusToggle(page), "the toolbar drew no Whisparr status control").toBeVisible({
    timeout: GRID_BUDGET_MS,
  });
}

test.describe("library status", () => {
  test("studio status behind the pill", async ({ page, baseUrl, libraryHarness }) => {
    // A container pair, an extension install, a browser and a real instance. Well above the shared
    // per-test budget, and deliberately its own number rather than a raised default for every spec.
    test.setTimeout(900_000);

    const coveApi = apiFor(libraryHarness);

    // Everything the browser reported, so a bundle-load throw is named by this spec rather than left
    // as a card that simply drew nothing.
    const consoleErrors = [];
    page.on("console", (message) => {
      if (message.type() === "error") consoleErrors.push(message.text());
    });
    page.on("pageerror", (failure) => {
      consoleErrors.push(String(failure));
    });

    await usingInstance(libraryHarness, coveApi, async () => {
      const { identified, unidentified } = await seedTwoStudios(coveApi);

      await openStudios(page, baseUrl);
      await expect(
        cardFor(page, identified.name),
        "the studios grid drew no card for the seeded studio",
      ).toBeVisible({ timeout: GRID_BUDGET_MS });

      // Off by default. A card with the control off is what a card with no extension registered
      // looks like, so nothing in the vocabulary may be on the page yet.
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
    });
  });

  test("the control changes colour, and one press costs one request", async ({
    page,
    baseUrl,
    libraryHarness,
  }) => {
    test.setTimeout(900_000);

    const coveApi = apiFor(libraryHarness);
    const requests = watchRequests(page);

    // No instance is started. What is measured here is the control's own appearance and what one
    // press costs on the network, and neither depends on what an instance answers.
    await connectNothing(coveApi);
    await seedTwoStudios(coveApi);
    await openStudios(page, baseUrl);

    // A class can be declared in the host stylesheet and still be dead: Cove declares one twice and
    // a later shorthand resets it. A clobbered class reads as the same colour in both states, which
    // is the fault this catches and the class check cannot.
    const colourNow = () => statusToggle(page).evaluate((el) => getComputedStyle(el).color);
    const off = await colourNow();

    await statusToggle(page).click();
    await expect(statusToggle(page)).toHaveAttribute("aria-pressed", "true");

    // Polled rather than read once. The control carries a colour transition, and a computed value
    // taken the instant the class changes is the interpolation at its start, which is the old colour.
    await expect
      .poll(colourNow, {
        timeout: COLOUR_SETTLE_MS,
        message: "the control reads the same colour on as off, so its state is invisible",
      })
      .not.toBe(off);
    const on = await colourNow();
    for (const [state, colour] of [
      ["off", off],
      ["on", on],
    ]) {
      expect(colour, `the control's ${state} colour is unset`).not.toBe("");
      expect(colour, `the control's ${state} colour is fully transparent`).not.toContain(", 0)");
    }

    // One request for the whole page, counted on the network rather than inferred.
    await page.waitForTimeout(SETTLE_DWELL_MS);
    expect(
      requests.toStatusRoute().length,
      "a page of studio cards did not fold into one request",
    ).toBe(1);

    // A display mode with no place for a badge mounts no card slot, so no card registers an
    // identifier and nothing is asked. This falls out of the coalescer's shape and costs nothing.
    await statusToggle(page).click();
    await page.getByRole("button", { name: "List", exact: true }).click();
    await expect(badgeStrip(page)).toHaveCount(0);

    const before = requests.toStatusRoute().length;
    await statusToggle(page).click();
    await page.waitForTimeout(SETTLE_DWELL_MS);

    expect(
      requests.toStatusRoute().length - before,
      "a press in a display mode with no badge asked for a status anyway",
    ).toBe(0);
  });

  test("nothing answered, so no card claims anything", async ({
    page,
    baseUrl,
    libraryHarness,
  }) => {
    test.setTimeout(900_000);

    const coveApi = apiFor(libraryHarness);

    // An address nothing answers, which is a different fact from nothing being configured: the
    // extension has an instance to ask and gets no answer.
    await connectNothing(coveApi);
    await seedTwoStudios(coveApi);
    await openStudios(page, baseUrl);

    await statusToggle(page).click();
    await page.waitForTimeout(SETTLE_DWELL_MS);

    // No badge at all. The unknown state belongs to the catalogue tab, and drawing it here would
    // report a read that failed as a status the instance gave.
    await expect(
      stateChips(page),
      "a card was given a state after nothing answered for it",
    ).toHaveCount(0);

    // The reason rides the control, once for the page, and the hover text is the same string.
    const spoken = `${HIDE_STATUS}. ${COULD_NOT_BE_READ}`;
    await expect(statusToggle(page), "the control states no reason at all").toHaveAttribute(
      "aria-label",
      spoken,
    );
    await expect(statusToggle(page)).toHaveAttribute("title", spoken);

    // Never disabled. An unreachable instance changes what the control says, not whether it works,
    // and pressing it again is the retry.
    await expect(statusToggle(page)).toBeEnabled();
    await statusToggle(page).click();
    await expect(statusToggle(page)).toHaveAttribute("aria-pressed", "false");
    await statusToggle(page).click();
    await expect(statusToggle(page)).toHaveAttribute("aria-pressed", "true");
  });

  test("reading a status changes nothing and reaches no provider", async ({
    page,
    baseUrl,
    libraryHarness,
  }) => {
    test.setTimeout(900_000);

    const coveApi = apiFor(libraryHarness);
    const requests = watchRequests(page);

    await usingInstance(libraryHarness, coveApi, async () => {
      const { identified } = await seedTwoStudios(coveApi);
      const before = await readMonitoring(coveApi, identified.id);

      await openStudios(page, baseUrl);
      await statusToggle(page).click();
      await expect(stateChips(cardFor(page, identified.name))).toHaveCount(1, {
        timeout: BADGE_BUDGET_MS,
      });
      await page.waitForTimeout(SETTLE_DWELL_MS);

      const asked = requests.toStatusRoute();
      expect(asked.length).toBeGreaterThan(0);
      expect(
        asked.filter((request) => request.method !== "POST"),
        "a status read used a method other than the one the route declares",
      ).toEqual([]);

      expect(
        await readMonitoring(coveApi, identified.id),
        "showing the status moved what the instance holds for the studio",
      ).toEqual(before);

      expect(requests.toProviders(), "showing the status reached a metadata provider").toEqual([]);
    });
  });

  test("only the grid display mode has a place for a badge", async ({
    page,
    baseUrl,
    libraryHarness,
  }) => {
    test.setTimeout(900_000);

    const coveApi = apiFor(libraryHarness);

    await usingInstance(libraryHarness, coveApi, async () => {
      const { identified } = await seedTwoStudios(coveApi);

      await openStudios(page, baseUrl);
      await statusToggle(page).click();
      await expect(stateChips(cardFor(page, identified.name))).toHaveCount(1, {
        timeout: BADGE_BUDGET_MS,
      });

      // Driven through the controls the page draws rather than trusted from this repository's own
      // reading of the host source. The card slot is mounted in the grid branch only.
      for (const mode of ["List", "Tagger"]) {
        await page.getByRole("button", { name: mode, exact: true }).click();
        await expect(
          badgeStrip(page),
          `a badge rendered in the ${mode} display mode, where the host mounts no card slot`,
        ).toHaveCount(0);
        await expect(
          statusToggle(page),
          `the control is absent in the ${mode} display mode`,
        ).toBeVisible();
      }

      await page.getByRole("button", { name: "Grid", exact: true }).click();
      await expect(
        stateChips(cardFor(page, identified.name)),
        "switching back to the grid did not bring the badge back without a second press",
      ).toHaveCount(1, { timeout: BADGE_BUDGET_MS });
    });

    // The control's own sentence for a mode with no place for a badge does not ship yet: that gap
    // first appears with the videos views, and its wording arrives with them.
    test.info().annotations.push({
      type: "narrowed-assertion",
      description:
        "the control's disclosure for a display mode with no badge is not asserted: that sentence is not yet declared in the shipped copy.",
    });
  });

  test("the badge strip stays one row inside the host's clipped box", async ({
    page,
    baseUrl,
    libraryHarness,
  }) => {
    test.setTimeout(900_000);

    const coveApi = apiFor(libraryHarness);

    await usingInstance(libraryHarness, coveApi, async () => {
      const { identified } = await seedTwoStudios(coveApi);

      await openStudios(page, baseUrl);

      // The narrowest card the host supports. Its own default is wider, so a strip that only fits
      // there would wrap on a reader's own setting and disappear below the clip with no error.
      await page.evaluate(() => {
        document.documentElement.style.setProperty("--card-min-width", "240px");
      });

      await statusToggle(page).click();
      const card = cardFor(page, identified.name);
      await expect(stateChips(card)).toHaveCount(1, { timeout: BADGE_BUDGET_MS });

      const strip = await badgeStrip(card).boundingBox();
      const chip = await stateChips(card).boundingBox();

      // The host clips its in-card box at 96px with paint containment, so a second row is not a
      // layout defect anyone sees: it simply vanishes.
      expect(
        strip.height,
        "the badge strip is taller than the box the host clips it at",
      ).toBeLessThan(96);

      // One row, measured as the strip being no taller than one chip plus its own padding.
      expect(
        strip.height,
        "the badge strip is taller than one chip and its padding, so it wrapped",
      ).toBeLessThanOrEqual(chip.height + 13);
    });
  });
});

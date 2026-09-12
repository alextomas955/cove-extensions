// What a library card says about the connected instance, in a real containerized host.
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
// WHAT IS NOT HERE. Switching the connected generation lives in generation-switch.spec.mjs beside
// this file. The connected generation is a global setting, so a test that changes it cannot share an
// installation with these.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { randomUUID } from "node:crypto";

import {
  expect,
  extensionRoute,
  seedCovePerformer,
  seedCoveStudio,
  seedCoveVideo,
  SETTLE_DWELL_MS,
  SPEC_BUDGET_MS,
  STASHDB_ENDPOINT,
  test,
} from "../../lib/connected-fixture.mjs";
import { visit } from "../../lib/steps.mjs";

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
 * What the control says in a display mode that mounts no card slot, transcribed the same way.
 *
 * The second sentence names the mode by the host's own visible label for it, so the reader has a
 * control to go and press rather than this repository's name for the mode.
 */
const NO_PLACE_FOR_A_BADGE =
  "This display mode has no place for a per-card status. Switch to the Grid display mode to see it.";

/** What the row says its counts are over, transcribed by hand from the shipped sentence. */
const COUNTS_ARE_FOR_THIS_PAGE =
  "These counts are for the cards on this page, not for the whole library.";

/** How many cards are seeded, which is more than one rendered window holds. */
const SEEDED_CARDS = 45;

/** How many times the end of the list is scrolled to before the loop gives up. */
const SCROLL_STEPS = 8;

/**
 * The five words a badge may read, transcribed by hand from the shipped vocabulary.
 *
 * A spec importing the constants the product declares would be asserting that a string equals
 * itself.
 */
const STATE_WORDS = ["Monitored", "Unmonitored", "Not added", "Excluded", "Status unknown"];

/** A real StashDB studio id. The library stores it, and the extension names the instance by it. */
const BRAZZERS_EXXTRA = "39cee498-a9ac-4403-910a-1a0157ad22d8";

const GRID_BUDGET_MS = 60_000;

/** Long enough for the control's own colour transition to finish. */
const COLOUR_SETTLE_MS = 10_000;
const BADGE_BUDGET_MS = 90_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });
test.use({ generation: "v3" });

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

/** The same route for the two card kinds the videos and performers pages draw. */
const SCENE_STATUS_ROUTE = extensionRoute("library/video/status");
const PERFORMER_STATUS_ROUTE = extensionRoute("library/performer/status");

/**
 * The metadata sources this product reads a catalogue from.
 *
 * Named here and nowhere in the shipped bundle: which source answers follows the connected
 * generation, so a name written into the product would be wrong on the other one.
 */
const PROVIDER_HOSTS = ["stashdb.org", "theporndb.net"];

/** How many cards one status request asked about, read off the body that left. */
function idsAsked(request) {
  return JSON.parse(request.body ?? "{}").coveIds?.length ?? 0;
}

/** Every request the page made, so a claim about what a press costs is read off the network. */
function watchRequests(page) {
  const seen = [];
  page.on("request", (request) => {
    seen.push({ url: request.url(), method: request.method(), body: request.postData() });
  });
  const to = (route) => seen.filter((request) => request.url.includes(route));
  return {
    toStatusRoute: () => to(STATUS_ROUTE),
    toSceneStatusRoute: () => to(SCENE_STATUS_ROUTE),
    toPerformerStatusRoute: () => to(PERFORMER_STATUS_ROUTE),
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
 * The chip's label shares its element with a drawn mark, so the element carries both and an
 * exact-text locator finds nothing. The shape is located here and the words are asserted off it.
 */
const stateChips = (scope) => scope.locator(".card-extension span.rounded-full");

/** The words a chip may read, as the element carries them: a glyph, then the label. */
const STATE_CHIP_TEXT = new RegExp(`(${STATE_WORDS.join("|")})$`);

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

/** Opens the studios grid and waits for the cards and the control to be on screen. */
async function openStudios(page, baseUrl) {
  await visit(page, baseUrl, "/studios", studioCards(page).first(), "the studios page");
  await expect(statusToggle(page), "the toolbar drew no Whisparr status control").toBeVisible({
    timeout: GRID_BUDGET_MS,
  });
}

/** Every card the videos grid drew, which the host gives its own class rather than the entity one. */
const videoCards = (page) => page.locator(".video-card");

/** One scene's own card, found by the title it was seeded under. */
const videoCardFor = (page, title) => videoCards(page).filter({ hasText: title });

/**
 * The row of counts under the toolbar, by the only thing that distinguishes it from the page's
 * other live regions: the sentence it carries about what it counted.
 */
const statusRow = (page) => page.locator(`[role=status][title="${COUNTS_ARE_FOR_THIS_PAGE}"]`);

/** The titles of the cards the videos grid holds right now, which is a window over the list. */
const cardTitles = (page) => videoCards(page).locator(".card-title").allInnerTexts();

/** Opens a list page in its default grid display mode, with the cards and the control on screen. */
async function openList(page, baseUrl, path, cards, where) {
  await visit(page, baseUrl, path, cards.first(), where);
  await expect(
    statusToggle(page),
    `${where}: the toolbar drew no Whisparr status control`,
  ).toBeVisible({ timeout: GRID_BUDGET_MS });
}

/**
 * Seeds one scene on both sides: the Cove video carrying its identity row, and the entry the
 * instance holds for it where `onInstance` says so.
 *
 * The instance's entry is written into its own datastore rather than added through its API: an add
 * resolves the identifier against the vendor's metadata service, so a scene's mere existence would
 * depend on someone else's uptime.
 */
async function seedScene(coveApi, whisparr, { label, onInstance, monitored = false }) {
  const remoteId = randomUUID();

  if (onInstance) {
    await whisparr.seedEntity("v3", {
      kind: "scene",
      foreignId: remoteId,
      title: `Whisparr ${label}`,
      monitored,
    });
  }

  const title = `${label} ${remoteId.slice(0, 8)}`;
  const video = await seedCoveVideo(coveApi, {
    title,
    remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId }],
  });
  return { ...video, title, remoteId };
}

/** Puts one identifier on the instance's own exclusion list, through the route that keeps it. */
async function excludeOnInstance(whisparr, remoteId, title) {
  const added = await whisparr.apiFor("v3").post("/api/v3/exclusions", {
    foreignId: remoteId,
    movieTitle: title,
    movieYear: 0,
  });
  expect(
    added.status,
    `POST /api/v3/exclusions answered ${String(added.status)}: ${String(added.text).slice(0, 300)}`,
  ).toBeLessThan(300);
}

test.describe("library status", () => {
  test("studio status behind the pill", async ({ page, baseUrl, connected }) => {
    const { api: coveApi } = connected;

    // Everything the browser reported, so a bundle-load throw is named by this spec rather than left
    // as a card that simply drew nothing.
    const consoleErrors = [];
    page.on("console", (message) => {
      if (message.type() === "error") consoleErrors.push(message.text());
    });
    page.on("pageerror", (failure) => {
      consoleErrors.push(String(failure));
    });

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

  test("the control changes colour, and one press costs one request", async ({
    page,
    baseUrl,
    api: coveApi,
  }) => {
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

  test("nothing answered, so no card claims anything", async ({ page, baseUrl, api: coveApi }) => {
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
    connected,
  }) => {
    const { api: coveApi } = connected;
    const requests = watchRequests(page);

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

  test("only the grid display mode has a place for a badge", async ({
    page,
    baseUrl,
    connected,
  }) => {
    const { api: coveApi } = connected;

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

      // Pressed and visibly on with nothing on screen is the same defect class as a dimmed
      // control with nothing to hear, so the control says why.
      const spoken = `${HIDE_STATUS}. ${NO_PLACE_FOR_A_BADGE}`;
      await expect(
        statusToggle(page),
        `the control states no reason in the ${mode} display mode, where no badge can appear`,
      ).toHaveAttribute("aria-label", spoken, { timeout: COLOUR_SETTLE_MS });
      await expect(statusToggle(page)).toHaveAttribute("title", spoken);
    }

    await page.getByRole("button", { name: "Grid", exact: true }).click();
    await expect(
      stateChips(cardFor(page, identified.name)),
      "switching back to the grid did not bring the badge back without a second press",
    ).toHaveCount(1, { timeout: BADGE_BUDGET_MS });

    // Back in a mode that draws them, the disclosure is gone: it is a fact about the display mode
    // and not a state the control latches.
    await expect(statusToggle(page)).toHaveAttribute("aria-label", HIDE_STATUS);
  });

  test("scene status behind the pill", async ({ page, baseUrl, connected }) => {
    const { api: coveApi, whisparr } = connected;

    const consoleErrors = [];
    page.on("console", (message) => {
      if (message.type() === "error") consoleErrors.push(message.text());
    });
    page.on("pageerror", (failure) => {
      consoleErrors.push(String(failure));
    });

    const monitored = await seedScene(coveApi, whisparr, {
      label: "Monitored scene",
      onInstance: true,
      monitored: true,
    });
    const unheld = await seedScene(coveApi, whisparr, {
      label: "Unheld scene",
      onInstance: false,
    });
    const excluded = await seedScene(coveApi, whisparr, {
      label: "Excluded scene",
      onInstance: false,
    });
    await excludeOnInstance(whisparr, excluded.remoteId, excluded.title);

    // No identity row at all, which is a card the extension cannot speak for rather than one the
    // instance holds nothing for.
    const unidentified = await seedCoveVideo(coveApi, {
      title: `Unidentified scene ${randomUUID().slice(0, 8)}`,
    });

    await openList(page, baseUrl, "/videos", videoCards(page), "the videos page");
    await expect(
      stateChips(page),
      "a state chip was on screen before the control was pressed",
    ).toHaveCount(0);

    await statusToggle(page).click();

    // The instance holds this one and monitors it, so the card carries the state that says so.
    const monitoredChip = stateChips(videoCardFor(page, monitored.title));
    await expect(
      monitoredChip,
      "the monitored scene's card carries no state chip, or carries more than one",
    ).toHaveCount(1, { timeout: BADGE_BUDGET_MS });
    await expect(monitoredChip).toHaveText(/Monitored$/);

    await expect(
      stateChips(videoCardFor(page, unheld.title)),
      "the scene the instance holds no entry for reads as something else",
    ).toHaveText(/Not added$/, { timeout: BADGE_BUDGET_MS });

    // Exclusion is asked before a state is derived, so a scene that is both excluded and unheld
    // reads as excluded. Both halves are asserted: the state it takes, and the one it must not.
    const excludedChip = stateChips(videoCardFor(page, excluded.title));
    await expect(excludedChip, "the excluded scene does not read as excluded").toHaveText(
      /Excluded$/,
      { timeout: BADGE_BUDGET_MS },
    );
    await expect(
      excludedChip,
      "the excluded scene reads as one the instance was never offered",
    ).not.toHaveText(/Not added$/);

    // An absence watched for a fixed dwell. A chip that has not rendered yet is otherwise
    // indistinguishable from one that never will.
    await page.waitForTimeout(SETTLE_DWELL_MS);
    const silent = videoCardFor(page, unidentified.title);
    await expect(
      stateChips(silent),
      "a scene Cove holds no usable link for was given a state anyway",
    ).toHaveCount(0);
    // The host draws its own entry wrapper for any registered slot, so the assertion is that the
    // wrapper holds nothing rather than that it is absent.
    await expect(
      badgeStrip(silent),
      "a scene Cove holds no usable link for drew a badge element anyway",
    ).toBeEmpty();

    // Otherwise unchanged. A card that lost its own title, or drew the host's error boundary in
    // place of its body, is a worse outcome than a wrong state.
    await expect(
      silent.locator(".card-title"),
      "the unidentified scene's card lost its own title",
    ).toHaveText(unidentified.title);
    await expect(
      silent.locator(".card-body"),
      "the unidentified scene's card lost its own body",
    ).toBeVisible();

    // The partial page, stated as one fact: resolved and unresolved cards coexist, and nothing
    // that failed to resolve is reported as an absence the instance stated.
    const drawn = await stateChips(page).count();
    expect(drawn, "no scene card on the page carries a state at all").toBeGreaterThan(0);
    expect(
      drawn,
      "every card on the page carries a state, so the unresolved case is not on it",
    ).toBeLessThan(await videoCards(page).count());

    // No state on the page rides on colour: each chip carries its own mark beside its label.
    const chips = await stateChips(page).all();
    for (const chip of chips) {
      await expect(
        chip.locator("svg[aria-hidden='true']"),
        "a state chip carries no mark, so it is distinguished by colour alone",
      ).toHaveCount(1);
    }

    // The row under the toolbar names the states the badges below it draw. The slot name, the
    // manifest's component name and the bundle's registered key are three strings across two
    // repositories, and a page where any pair differs draws no row at all, with no error.
    const row = statusRow(page);
    await expect(row, "no row of counts is under the toolbar").toHaveCount(1, {
      timeout: BADGE_BUDGET_MS,
    });

    // Read as counts rather than as a sentence: each is the count of cards on the page carrying
    // that state, and the seeded page has one of each.
    await expect(row, "the row does not count the monitored scene").toContainText(/1\s*Monitored/);
    await expect(row, "the row does not count the excluded scene").toContainText(/1\s*Excluded/);
    await expect(
      row,
      "the row does not count the scene the instance holds no entry for",
    ).toContainText(/1\s*not added on this page/);

    const missingComponent = consoleErrors.filter((line) =>
      /component not found|does not provide an export|SyntaxError/i.test(line),
    );
    expect(
      missingComponent,
      `a page reported a component the bundle does not register: ${missingComponent.join(" | ")}`,
    ).toEqual([]);
  });

  test("the row of counts appears with the badges and leaves with them", async ({
    page,
    baseUrl,
    connected,
  }) => {
    const { api: coveApi, whisparr } = connected;

    await seedScene(coveApi, whisparr, {
      label: "Monitored scene",
      onInstance: true,
      monitored: true,
    });

    await openList(page, baseUrl, "/videos", videoCards(page), "the videos page");

    // Nothing before the control is pressed. A row of zeroes on a page nobody asked about is a
    // report over a read that never happened.
    await expect(
      statusRow(page),
      "a row of counts was under the toolbar before the control was pressed",
    ).toHaveCount(0);

    await statusToggle(page).click();
    await expect(statusRow(page), "no row appeared with the badges").toHaveCount(1, {
      timeout: BADGE_BUDGET_MS,
    });

    await statusToggle(page).click();
    await expect(statusRow(page), "the row outlived the badges it counts").toHaveCount(0, {
      timeout: BADGE_BUDGET_MS,
    });
  });

  test("a rendered window of scene cards folds into one request", async ({
    page,
    baseUrl,
    api: coveApi,
  }) => {
    const requests = watchRequests(page);

    // No instance is started. What is measured is what the page costs on the network, which does not
    // depend on what an instance answers.
    await connectNothing(coveApi);

    // More cards than one window holds, so there is a second window to scroll into. Each carries an
    // identity row, because a card with none registers nothing and would understate the cost this
    // measures.
    for (let seeded = 0; seeded < SEEDED_CARDS; seeded++) {
      await seedCoveVideo(coveApi, {
        title: `Windowed ${String(seeded).padStart(3, "0")} ${randomUUID().slice(0, 8)}`,
        remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: randomUUID() }],
      });
    }

    // The unbounded page size, named in the address so the display mode stays the grid the card slot
    // is mounted in. The host then renders a window of the list and moves that window as the reader
    // scrolls, rather than holding every card it has fetched.
    await openList(page, baseUrl, "/videos?perPage=0", videoCards(page), "the videos page");

    // The window the host settles on. It renders what its own viewport reaches, so what is on screen
    // is a fact about the run rather than a number to transcribe.
    const settledTitles = async () => {
      let held = [];
      for (let read = await cardTitles(page); read.length !== held.length;) {
        held = read;
        await page.waitForTimeout(1_000);
        read = await cardTitles(page);
      }
      return held;
    };

    const firstWindow = await settledTitles();
    expect(firstWindow.length, "the host drew no card to press the control over").toBeGreaterThan(
      1,
    );

    await statusToggle(page).click();
    await page.waitForTimeout(SETTLE_DWELL_MS);

    const first = requests.toSceneStatusRoute();
    expect(first.length, "one rendered window of scene cards did not fold into one request").toBe(
      1,
    );
    expect(idsAsked(first[0]), "the one request did not carry every card the window drew").toBe(
      firstWindow.length,
    );

    // Scrolled to the end of the list, so the host moves its window and mounts cards the first one
    // never held.
    const seen = new Set(firstWindow);
    for (let step = 0; step < SCROLL_STEPS; step++) {
      await videoCards(page).last().scrollIntoViewIfNeeded();
      const now = await settledTitles();
      const fresh = now.filter((title) => !seen.has(title));
      for (const title of now) seen.add(title);
      await page.waitForTimeout(SETTLE_DWELL_MS);
      if (fresh.length === 0) break;
    }

    expect(
      seen.size,
      "the host mounted no card the first window did not hold, so nothing was measured",
    ).toBeGreaterThan(firstWindow.length);

    // Every request folds a window, and none is a request for one card. A page of cards costing one
    // request each is the fault this measures, and it is stated per request rather than as a total:
    // how many windows the host renders on the way down is the host's to decide.
    const asked = requests.toSceneStatusRoute();
    expect(
      asked.map(idsAsked).filter((carried) => carried < 2),
      "a status request carried a single card, so the page did not fold into batches",
    ).toEqual([]);
    expect(
      asked.length,
      `${String(seen.size)} cards cost ${String(asked.length)} requests, which is one per card rather than one per window`,
    ).toBeLessThan(seen.size);
  });

  test("performer cards carry the same badge from the same request", async ({
    page,
    baseUrl,
    connected,
  }) => {
    const { api: coveApi, whisparr } = connected;
    const requests = watchRequests(page);

    const foreignId = randomUUID();
    await whisparr.seedEntity("v3", {
      kind: "performer",
      foreignId,
      title: `Whisparr Performer ${foreignId.slice(0, 8)}`,
      monitored: true,
    });
    const performer = await seedCovePerformer(coveApi, {
      name: `Performer ${foreignId.slice(0, 8)}`,
      remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: foreignId }],
    });

    await openList(page, baseUrl, "/performers", studioCards(page), "the performers page");
    await statusToggle(page).click();

    const chip = stateChips(cardFor(page, performer.name));
    await expect(
      chip,
      "the performer's card carries no state chip, or carries more than one",
    ).toHaveCount(1, { timeout: BADGE_BUDGET_MS });
    await expect(
      chip,
      "the performer's chip reads something outside this product's own five-state vocabulary",
    ).toHaveText(STATE_CHIP_TEXT);

    await page.waitForTimeout(SETTLE_DWELL_MS);
    expect(
      requests.toPerformerStatusRoute().length,
      "a page of performer cards did not fold into one request",
    ).toBe(1);
  });

  test("the badge strip stays one row inside the host's clipped box", async ({
    page,
    baseUrl,
    connected,
  }) => {
    const { api: coveApi } = connected;

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

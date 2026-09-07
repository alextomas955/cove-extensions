// The catalogue card's three controls in a real containerized host: where the keyboard goes, what
// each control paints, and what a press leaves behind.
//
// WHY THIS SPEC EXISTS. The unit suite runs in a node environment and renders no `.tsx`, so a focus
// order, a painted ring and a rendered height are provable nowhere else. None of it was reachable
// before the grid mounted each card with its selection and verb handlers.
//
// WHERE THE CATALOGUE COMES FROM. The tab's own page read is answered at the network with a
// recorded page. The grid is otherwise fed by a live provider read with no seam for a 500-character
// title, and the scenes have to be the same on every run for a focus walk to mean anything.
// Intercepting the extension's own route keeps that seam inside this file: the shipped bundle
// carries no test hook, and the host, the extension bundle, the tab and the verb routes are all the
// real ones.
//
// WHICH PRESSES REACH THE INSTANCE. The Search case does: a scene the connected Whisparr holds no
// entry for is an answer only the server can give, and it is the one this spec drives end to end.
// The two Monitor cases are answered at the network, because what is under test there is the dimmed
// control, the settled pill and the tone the refusal reads in. What the server answers for each
// refusal is covered by its own integration suite.
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
  STASHDB_ENDPOINT,
  WHISPARR_ROOT,
  WHISPARR_SYNC_EXTENSION,
} from "../lib/whisparr-sync-fixtures.mjs";

const TAB_LABEL = "Missing";

// The sentences and control names this spec asserts on, transcribed by hand from the shipped copy.
// A spec importing the constants would be asserting that a string equals itself.
const SEARCH_WITH_NO_ENTRY =
  "Whisparr has no entry for this scene yet, so there is nothing to search for";
const INSTANCE_REFUSED = "Whisparr would not do this. Nothing here was changed.";
const SELECT_SCENE = "Select scene";
const DESELECT_SCENE = "Deselect scene";
const MONITOR = "Monitor";
const SEARCH = "Search";
// The pill draws its glyph and this word inside one element, so no element carries the word
// alone and a match on it has to be a substring one.
const WANTED = "Wanted";

const BUNDLE_BUDGET_MS = 60_000;
const BUNDLE_ATTEMPTS = 3;
const TAB_BUDGET_MS = 30_000;
const REGION_BUDGET_MS = 90_000;

/** How far a Tab walk is allowed to run before it is reported as never having reached the grid. */
const MAX_TAB_PRESSES = 400;

/** A title and a studio name far past anything the layout was drawn for. */
const HOSTILE_TITLE = `Ω${"the quick brown fox jumps over the lazy dog ".repeat(12)}`.slice(0, 500);
const HOSTILE_STUDIO = "Å".repeat(200);

const test = base.extend({
  cardHarness: [
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

  // Read through the handle AFTER the install, which restarts the container and can republish it on
  // a different host port.
  baseUrl: async ({ cardHarness }, use) => {
    await use(cardHarness.baseUrl);
  },

  whisparrV3: [
    async ({ cardHarness }, use) => {
      const whisparr = await startWhisparr({
        network: cardHarness.container.getNetworkNames()[0],
        generations: ["v3"],
      });
      try {
        whisparr.v3.rootFolder = await registerRootFolder(
          whisparr.v3.container,
          whisparr.apiFor("v3"),
          "v3",
          WHISPARR_ROOT,
        );
        await use(whisparr);
      } finally {
        await whisparr.stop();
      }
    },
    { scope: "test" },
  ],
});

const missingTab = (page) => page.getByRole("tab", { name: TAB_LABEL }).first();
const hostDetailTabs = (page) => page.getByRole("tablist").first();
const cards = (page) => page.locator("article").filter({ has: page.locator("img, h3") });

/** One recorded scene, with only what the card draws from. */
function scene(overrides) {
  return {
    providerSceneId: randomUUID(),
    title: "A scene the library does not hold",
    releaseDate: "2019-04-02",
    coverUrl: null,
    studioName: "A studio",
    description: null,
    performers: [],
    tags: [],
    performerCount: 0,
    tagCount: 0,
    state: "notAdded",
    ...overrides,
  };
}

/** One recorded page carrying `sceneList`, answered in place of the provider read. */
function recordedPage(sceneList) {
  return {
    cards: sceneList,
    catalogueSize: sceneList.length,
    sizeIsLowerBound: false,
    page: 1,
    perPage: 40,
    lastPage: 1,
    rangeFrom: 1,
    rangeTo: sceneList.length,
    refusal: "none",
    facets: [],
    sorts: [],
    sortInForce: null,
    statusWasRead: true,
    statusIsPermanentlyAbsent: false,
    providerName: "StashDB",
  };
}

/** Opens `path`, re-navigating while nothing the caller named has rendered. */
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

/** What the focused element is called, and whether it lies inside the card at `index`. */
async function focusedStop(page, index) {
  return page.evaluate((cardIndex) => {
    const card = document.querySelectorAll("article")[cardIndex];
    const active = document.activeElement;
    if (!card || !active || !card.contains(active)) return null;
    const name = active.getAttribute("aria-label") ?? active.textContent ?? "";
    return {
      name: name.trim(),
      shadow: window.getComputedStyle(active).boxShadow,
    };
  }, index);
}

test("missing card: the three controls, the keyboard walk through them and what each paints", async ({
  page,
  baseUrl,
  cardHarness,
  whisparrV3,
}) => {
  // A container pair, an extension install and a browser, well above the shared per-test budget.
  test.setTimeout(900_000);

  const coveApi = createApiClient(
    () => cardHarness.baseUrl,
    () => cardHarness.token,
  );
  await connectWhisparr(coveApi, whisparrV3, "v3");

  const studio = await seedCoveStudio(coveApi, {
    name: `Card studio ${randomUUID().slice(0, 8)}`,
    remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: randomUUID() }],
  });

  const drawn = [
    scene({ title: HOSTILE_TITLE, studioName: HOSTILE_STUDIO }),
    scene({ title: "An ordinary neighbour" }),
    scene({ title: "A second ordinary neighbour" }),
  ];

  let pageReadWasIntercepted = false;
  await page.route(/\/missing\?/, async (route) => {
    pageReadWasIntercepted = true;
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(recordedPage(drawn)),
    });
  });

  await visit(
    page,
    baseUrl,
    `/studio/${String(studio.id)}`,
    hostDetailTabs(page),
    "the studio detail page",
  );
  await expect(missingTab(page)).toBeVisible({ timeout: TAB_BUDGET_MS });
  await missingTab(page).click();
  await expect(cards(page).first()).toBeVisible({ timeout: REGION_BUDGET_MS });
  expect(
    pageReadWasIntercepted,
    "the recorded page never arrived, so every case below would have run against whatever the provider answered",
  ).toBe(true);
  await expect(cards(page)).toHaveCount(drawn.length);

  // CASE 7, taken first because it is the only one a press could disturb. A hostile title and a
  // hostile studio name leave the card the same height as its neighbours.
  const heights = [];
  for (let index = 0; index < drawn.length; index++) {
    const box = await cards(page).nth(index).boundingBox();
    heights.push(box?.height ?? null);
  }
  expect(
    heights[0],
    "a 500-character title and a 200-character studio name stretched the card past its neighbours",
  ).toBeCloseTo(heights[1], 0);
  expect(heights[1]).toBeCloseTo(heights[2], 0);

  // CASE 1. Focus order within one card is exactly three stops, in the order checkbox, Monitor,
  // Search, walked from the keyboard with no pointer interaction inside the grid first.
  const walk = [];
  let entered = false;
  for (let press = 0; press < MAX_TAB_PRESSES; press++) {
    await page.keyboard.press("Tab");
    const stop = await focusedStop(page, 0);
    if (stop === null) {
      if (entered) break;
      continue;
    }
    entered = true;
    walk.push(stop);
  }

  expect(
    walk.map((stop) => stop.name),
    "the card offers three stops, in the order the design contract fixes",
  ).toEqual([SELECT_SCENE, MONITOR, SEARCH]);

  // CASE 2. Each of those three paints a ring. Read as a computed box shadow, because the class
  // being present in the markup says nothing about whether the host's stylesheet declares it.
  for (const stop of walk) {
    expect(
      stop.shadow,
      `${stop.name} shows a keyboard user nothing: its focus ring paints no box shadow`,
    ).not.toBe("none");
    expect(stop.shadow?.length ?? 0).toBeGreaterThan(0);
  }

  // CASE 3. A ticked card draws its selection ring and its control reads as pressed.
  const firstCard = cards(page).first();
  await firstCard.getByRole("button", { name: SELECT_SCENE }).click();
  await expect(firstCard.getByRole("button", { name: DESELECT_SCENE })).toHaveAttribute(
    "aria-pressed",
    "true",
  );
  await expect(
    firstCard,
    "a ticked card carries no ring, so a selection is invisible on the card it was made on",
  ).toHaveClass(/ring-2/);
  await expect(cards(page).nth(1)).not.toHaveClass(/ring-2/);

  // CASE 5, before the recorded verb answers are installed. A search on a scene the connected
  // instance holds no entry for is answered by the server itself.
  await cards(page).nth(1).getByRole("button", { name: SEARCH }).click();
  const noEntry = cards(page).nth(1).getByText(SEARCH_WITH_NO_ENTRY, { exact: false });
  await expect(noEntry, "the instance holds no entry for this scene, and the card did not say so", {
    timeout: REGION_BUDGET_MS,
  }).toBeVisible();
  await expect(
    noEntry,
    "a true statement about the instance was painted in the tone a failed action uses",
  ).toHaveClass(/text-secondary/);
  await expect(noEntry).not.toHaveClass(/text-red-400/);

  // CASE 4. Pressing Monitor dims that card's Monitor and no other card's, then settles into the
  // pill. The answer is held in flight, so the assertion is about what the card does while it waits
  // rather than about how fast the instance answers.
  let held = null;
  let holdTheNextPress = true;
  await page.route(/\/missing\/[^/]+\/monitor$/, async (route) => {
    if (holdTheNextPress) {
      holdTheNextPress = false;
      held = route;
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ state: "notAdded", refusal: "instanceRefused" }),
    });
  });

  const pressed = cards(page).nth(1);
  await pressed.getByRole("button", { name: MONITOR }).click();
  await expect(pressed.getByRole("button", { name: MONITOR })).toBeDisabled();
  await expect(
    cards(page).nth(2).getByRole("button", { name: MONITOR }),
    "a press on one card dimmed another card's control, so two cards mid-flight would overwrite each other",
  ).toBeEnabled();

  await held.fulfill({
    status: 200,
    contentType: "application/json",
    body: JSON.stringify({ state: "monitored", refusal: "none" }),
  });
  await expect(pressed.getByText(WANTED, { exact: false })).toBeVisible({
    timeout: REGION_BUDGET_MS,
  });

  // CASE 6. A press the instance refuses puts the card back as it was and states the reason beneath
  // the action row.
  const refused = cards(page).nth(2);
  await refused.getByRole("button", { name: MONITOR }).click();
  const reason = refused.getByText(INSTANCE_REFUSED, { exact: false });
  await expect(reason, "a refused press said nothing at all beneath the action row").toBeVisible({
    timeout: REGION_BUDGET_MS,
  });
  await expect(
    reason,
    "the instance declined, which is a failure and reads in the failure tone",
  ).toHaveClass(/text-red-400/);
  await expect(
    refused.getByText(WANTED, { exact: false }),
    "a refused press left the optimistic pill on screen, claiming a state the instance declined",
  ).toHaveCount(0);

  await page.unroute(/\/missing\/[^/]+\/monitor$/);
  await page.unroute(/\/missing\?/);
});

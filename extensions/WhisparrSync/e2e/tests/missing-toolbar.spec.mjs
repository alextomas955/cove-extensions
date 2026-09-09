// The catalogue toolbar driven in a real containerized host.
//
// WHY THIS SPEC EXISTS. The unit tests for this toolbar run in a node environment and render no
// `.tsx`, so every claim about typing, focus, keyboard and the address bar is unproven until a real
// browser makes it. This spec is where those claims are settled.
//
// WHAT IT NEEDS. A Cove container and an installed extension, and nothing else: the search field and
// Refresh are drawn before any page has answered, so no Whisparr instance and no provider credential
// are involved.
//
// WHAT SKIPS, AND WHY. Three assertions need a control that only exists once a catalogue page has
// answered into the toolbar - the ordering menu, the facet menus and the count line beneath them.
// Each names its reason in an annotation, so a green run is not read as covering more than it did.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { randomUUID } from "node:crypto";

import {
  test as base,
  expect,
  seedCoveStudio,
  WHISPARR_SYNC_EXTENSION,
} from "../lib/whisparr-sync-fixtures.mjs";

/** The tab's label, transcribed by hand from the manifest that advertises it. */
const TAB_LABEL = "Missing";

/** The toolbar's own control names, transcribed from the shipped labels the same way. */
const SEARCH_PLACEHOLDER = "Search titles";
const REFRESH_LABEL = "Refresh";

/** The placeholder in a menu's own search box, transcribed the same way. */
const MENU_SEARCH_PLACEHOLDER = "Search this menu";

/**
 * The keys Cove deletes from the address on every tab change, including the change into this tab.
 *
 * Transcribed by hand from the host's own list URL hook. This tab's keys carry a prefix so that none
 * of them is one of these, and none of this tab's state may travel under one of these names.
 */
const LIST_URL_MANAGED_KEYS = [
  "q",
  "page",
  "perPage",
  "sort",
  "direction",
  "sorts",
  "view",
  "viewMode",
  "filters",
  "seed",
  "searchMode",
];

/** What the reader types, long enough that one keystroke per history entry would be obvious. */
const TYPED_SEARCH = "sunrise";

/**
 * The source the answered page below names itself as, which is not the one a v3 connection reads.
 *
 * Chosen for that reason: a name the toolbar took from anywhere but the answered page would read as
 * the other one.
 */
const ANSWERED_SOURCE = "ThePornDB";
const OTHER_SOURCE = "StashDB";

/**
 * What the line under the bar says the total counts, transcribed by hand with its two slots filled.
 * The total itself is stated in the bar.
 */
const COUNT_LINE = `That total is the scenes ${ANSWERED_SOURCE} lists for this studio, not the number you are missing.`;

/** The range the answered page below covers, in the wording the bar states it in. */
const RANGE_IN_THE_BAR = "1–40 of 272";

const BUNDLE_BUDGET_MS = 60_000;
const BUNDLE_ATTEMPTS = 3;
const TAB_BUDGET_MS = 30_000;
const SETTLE_BUDGET_MS = 30_000;

const test = base.extend({
  toolbarHarness: [
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
  baseUrl: async ({ toolbarHarness }, use) => {
    await use(toolbarHarness.baseUrl);
  },
});

const missingTab = (page) => page.getByRole("tab", { name: TAB_LABEL }).first();
const hostDetailTabs = (page) => page.getByRole("tablist").first();
const searchField = (page) => page.getByPlaceholder(SEARCH_PLACEHOLDER);
const refreshControl = (page) => page.getByRole("button", { name: REFRESH_LABEL });

/**
 * The bar itself, reached by its role and the name it carries.
 *
 * Scoped rather than unscoped because the entity hero above this tab draws a menu control of its
 * own, and an unscoped menu-trigger locator finds that one first. Reached by the bar's own name
 * rather than through a control inside it: every control in it now names the value in force, so a
 * control's name changes when a filter is applied and the name of the bar does not.
 */
const toolbar = (page) => page.getByRole("toolbar", { name: TAB_LABEL });

/**
 * The ordering control, named by the menu it belongs to and then by the ordering in force. The
 * leading name is off screen and is the half that does not change with the ordering.
 */
const sortControl = (page) => toolbar(page).getByRole("button", { name: /^Sort/ });
const facetChip = (page) => toolbar(page).locator('[aria-haspopup="menu"]');

/**
 * Opens `path`, re-navigating while nothing the caller named has rendered.
 *
 * The host carries an unknown key only until it finishes loading extensions, then rewrites the
 * address to its first built-in tab, and it paints its own error boundary in place of a page whose
 * lazily-imported chunk failed to fetch. Only a fresh navigation recovers either, and the retry is
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

/** Opens the entity page and clicks into the catalogue tab. */
async function openTheTab(page, baseUrl, path, where) {
  await visit(page, baseUrl, path, hostDetailTabs(page), where);
  await expect(
    missingTab(page),
    `${where}: the host drew its own detail tabs and no ${TAB_LABEL} tab.`,
  ).toBeVisible({ timeout: TAB_BUDGET_MS });
  await missingTab(page).click();
  await expect(searchField(page), `${where}: the tab mounted and drew no toolbar.`).toBeVisible({
    timeout: TAB_BUDGET_MS,
  });
}

/** Every key the address carries. */
async function addressState(page) {
  return page.evaluate(() => {
    const params = new URLSearchParams(window.location.search);
    return Object.fromEntries(params.entries());
  });
}

/**
 * The keys this tab owns.
 *
 * The host's own keys are excluded because it deletes them on every tab change, so comparing them
 * across one would be asserting the host's behaviour rather than this tab's.
 */
async function tabState(page) {
  const address = await addressState(page);
  return Object.fromEntries(Object.entries(address).filter(([key]) => key.startsWith("wsm")));
}

/** How the control looks while it does not have focus, and while it does. */
async function focusTreatment(locator) {
  const read = () =>
    locator.evaluate((element) => {
      const style = window.getComputedStyle(element);
      return `${style.boxShadow}|${style.borderColor}|${style.outlineStyle}`;
    });

  const blurred = await read();
  await locator.focus();
  return { blurred, focused: await read() };
}

function annotate(description) {
  test.info().annotations.push({ type: "skipped-assertion", description });
}

test("the toolbar round-trips through the page URL, and its controls are reachable", async ({
  page,
  baseUrl,
  toolbarHarness,
}) => {
  // A container pair, an extension install and a browser. Well above the shared per-test budget.
  test.setTimeout(600_000);

  const coveApi = createApiClient(
    () => toolbarHarness.baseUrl,
    () => toolbarHarness.token,
  );

  const studio = await seedCoveStudio(coveApi, {
    name: `Studio ${randomUUID().slice(0, 8)}`,
    remoteIds: [],
  });

  const studioPath = `/studio/${String(studio.id)}`;
  await openTheTab(page, baseUrl, studioPath, "the studio detail page");

  // 1. Typing settles and rewrites the address ONCE. A history entry per keystroke would leave the
  //    back button stepping through half-typed searches.
  const historyBefore = await page.evaluate(() => window.history.length);
  await searchField(page).pressSequentially(TYPED_SEARCH, { delay: 30 });
  await expect
    .poll(async () => (await tabState(page)).wsmQ, {
      timeout: SETTLE_BUDGET_MS,
      message: "the search never reached the address, so nothing settled",
    })
    .toBe(TYPED_SEARCH);
  const historyAfter = await page.evaluate(() => window.history.length);
  expect(
    historyAfter,
    `typing ${String(TYPED_SEARCH.length)} characters added ${String(historyAfter - historyBefore)} history entries`,
  ).toBe(historyBefore);

  // 2. This tab's state travels under this tab's own keys, and under none of the host's.
  const afterSearch = await tabState(page);
  const wholeAddress = await addressState(page);
  for (const key of LIST_URL_MANAGED_KEYS) {
    expect(
      wholeAddress[key],
      `the search travelled under the host's own "${key}", which the host deletes on every tab change`,
    ).not.toBe(TYPED_SEARCH);
  }

  const sortTriggers = await sortControl(page).count();
  if (sortTriggers > 0) {
    await sortControl(page).first().click();
    await expect(page.getByRole("menu")).toBeVisible();
    await page.getByRole("menuitemcheckbox").nth(1).click();
    await expect
      .poll(async () => (await tabState(page)).wsmSort, { timeout: SETTLE_BUDGET_MS })
      .toBeTruthy();
  } else {
    annotate(
      "the ordering assertions did not run: no page had answered into the toolbar, so it drew no ordering menu",
    );
  }

  // 3. The address is the whole of what a shared link carries: reloading it restores the view.
  const shared = page.url();
  await visit(page, "", shared, hostDetailTabs(page), "the shared link");
  await missingTab(page).click();
  await expect(
    searchField(page),
    "the shared link did not restore the search it carried",
  ).toHaveValue(TYPED_SEARCH, { timeout: TAB_BUDGET_MS });
  expect(
    await tabState(page),
    "the reloaded address does not describe the view it was copied from",
  ).toEqual(afterSearch);

  // 4. A facet change moves the number the bar states, not only the cards.
  annotate(
    "the range assertion did not run: the bar states a range only once a page has answered, and none had",
  );

  // 5. The host deletes its own keys on a tab change, including the change back into this tab. This
  //    tab's prefixed keys are what survive that.
  const hostTab = page.getByRole("tab").filter({ hasNotText: TAB_LABEL }).first();
  await hostTab.click();
  await missingTab(page).click();
  await expect(searchField(page)).toBeVisible({ timeout: TAB_BUDGET_MS });
  expect(
    (await tabState(page)).wsmQ,
    "leaving the tab and returning lost this tab's own state",
  ).toBe(TYPED_SEARCH);

  // 6. A menu meets Cove's keyboard bar: a menu role, a caret that lands in its search box, arrow-key
  //    roving focus into the rows, Escape closing it and returning focus to the control that opened it.
  const facetTriggers = await facetChip(page).count();
  if (facetTriggers > 0) {
    const trigger = facetChip(page).first();
    await trigger.click();
    const menu = page.getByRole("menu");
    await expect(menu).toBeVisible();

    const search = page.getByPlaceholder(MENU_SEARCH_PLACEHOLDER);
    await expect(search, "the menu opened with the caret somewhere else").toBeFocused();

    // Typed rather than filled: a keystroke is what the roving focus could swallow.
    await page.keyboard.type("zz");
    await expect(search, "the menu's own arrow-key handling took the typed characters").toHaveValue(
      "zz",
    );

    await search.fill("");
    await page.keyboard.press("ArrowDown");
    const focusedRole = await page.evaluate(
      () => document.activeElement?.getAttribute("role") ?? "",
    );
    expect(focusedRole, "the arrow keys did not step from the search box into the rows").toBe(
      "menuitemcheckbox",
    );

    await page.keyboard.press("Escape");
    await expect(menu).toBeHidden();
    await expect(trigger, "Escape closed the menu and left focus nowhere").toBeFocused();

    await trigger.click();
    await expect(page.getByRole("menu")).toBeVisible();
    await page.mouse.click(5, 5);
    await expect(page.getByRole("menu")).toBeHidden();
  } else {
    annotate(
      "the menu keyboard assertions did not run: no page had answered into the toolbar, so it drew no facet menu",
    );
  }

  // 7. Every control shows where the keyboard is. `focus-visible:ring-*` is absent from the host
  //    stylesheet and paints nothing, so this reads the rendered treatment.
  for (const [name, locator] of [
    ["the search field", searchField(page)],
    [REFRESH_LABEL, refreshControl(page)],
  ]) {
    const treatment = await focusTreatment(locator);
    expect(
      treatment.focused,
      `${name} looks the same focused as it does unfocused, so a keyboard user cannot see where they are`,
    ).not.toBe(treatment.blurred);
  }
});

/**
 * One page in the shape the extension's own route answers.
 *
 * It carries a card, because a page carrying none is an empty answer and the grid states that in
 * place of the count line this reads.
 */
function answeredPage() {
  return {
    cards: [
      {
        providerSceneId: randomUUID(),
        title: "A scene the library does not hold",
        releaseDate: "2019-04-02",
        coverUrl: null,
        studioName: null,
        description: null,
        performers: [],
        tags: [],
        performerCount: 0,
        tagCount: 0,
        state: "notAdded",
      },
    ],
    catalogueSize: 272,
    sizeIsLowerBound: false,
    page: 1,
    perPage: 40,
    lastPage: 7,
    rangeFrom: 1,
    rangeTo: 40,
    refusal: "none",
    facets: [],
    sorts: [],
    sortInForce: null,
    statusWasRead: true,
    statusIsPermanentlyAbsent: false,
    providerName: ANSWERED_SOURCE,
  };
}

test("an answered page decides the source a sentence names", async ({
  page,
  baseUrl,
  toolbarHarness,
}) => {
  // A container pair, an extension install and a browser. Well above the shared per-test budget.
  test.setTimeout(600_000);

  const coveApi = createApiClient(
    () => toolbarHarness.baseUrl,
    () => toolbarHarness.token,
  );
  const studio = await seedCoveStudio(coveApi, {
    name: `Studio ${randomUUID().slice(0, 8)}`,
    remoteIds: [],
  });

  // The page read is answered here, because the count line is not drawn until one has answered and
  // this harness connects no instance and holds no provider credential.
  let pageReadWasIntercepted = false;
  await page.route(/\/missing\?/, async (route) => {
    pageReadWasIntercepted = true;
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(answeredPage()),
    });
  });

  const studioPath = `/studio/${String(studio.id)}`;
  await openTheTab(page, baseUrl, studioPath, "the studio detail page");
  await expect(page.getByText(COUNT_LINE)).toBeVisible({ timeout: SETTLE_BUDGET_MS });
  expect(
    pageReadWasIntercepted,
    "the answered page never arrived, so the assertion below would have run against a refusal",
  ).toBe(true);

  // The range is stated in the bar and nowhere else. Two copies of it disagree the moment a facet
  // moves one of them, and the reader has no way to tell which one is the page they are on.
  await expect(
    page.getByText(RANGE_IN_THE_BAR),
    "the answered page's range is drawn more than once, or nowhere",
  ).toHaveCount(1);
  await expect(
    toolbar(page).getByText(RANGE_IN_THE_BAR),
    "the range is drawn somewhere other than the bar",
  ).toBeVisible();

  // The sentence names the source the page carried, and names the other one nowhere.
  await expect(
    page.getByText(OTHER_SOURCE),
    `a sentence named ${OTHER_SOURCE} on a page answered by ${ANSWERED_SOURCE}`,
  ).toHaveCount(0);

  await page.unroute(/\/missing\?/);
});

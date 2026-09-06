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
const MONITOR_ALL_LABEL = "Monitor all";

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
 * The control row itself, reached through the one control it always draws.
 *
 * Scoped this way because the entity hero above this tab draws a menu control of its own, and an
 * unscoped menu-trigger locator finds that one first.
 */
const toolbar = (page) => refreshControl(page).locator("xpath=..");
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
  const tag = await coveApi.post("/api/tags", { name: `Tag ${randomUUID().slice(0, 8)}` });
  expect(tag.status, `POST /api/tags answered ${String(tag.status)}`).toBeLessThan(300);

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

  // 4. A facet change moves the number the count line states, not only the cards.
  annotate(
    "the count-line assertion did not run: the count line is drawn beneath this toolbar and no page had answered one",
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

  // 6. A menu meets Cove's keyboard bar: a menu role, arrow-key roving focus, Escape closing it and
  //    returning focus to the control that opened it.
  const facetTriggers = await facetChip(page).count();
  if (facetTriggers > 0) {
    const trigger = facetChip(page).first();
    await trigger.click();
    const menu = page.getByRole("menu");
    await expect(menu).toBeVisible();

    const first = await page.evaluate(() => document.activeElement?.textContent ?? "");
    await page.keyboard.press("ArrowDown");
    const second = await page.evaluate(() => document.activeElement?.textContent ?? "");
    expect(second, "the arrow keys moved focus nowhere inside the menu").not.toBe(first);

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

  // 8. Whisparr expresses no whole-tag action, so a tag page carries no such control anywhere.
  await openTheTab(page, baseUrl, `/tag/${String(tag.json.id)}`, "the tag detail page");
  await expect(
    page.getByRole("button", { name: MONITOR_ALL_LABEL }),
    "a tag page drew a whole-view action, which Whisparr cannot express for a tag",
  ).toHaveCount(0);
});

// Ticking a page of scenes and marking them wanted, in a real containerized host.
//
// WHY THIS SPEC EXISTS. Two of these properties are provable nowhere else. The first is that Cove's
// own list shortcuts reach a component an extension mounted inside a detail page: the hook is
// exported to extensions, but no extension had registered a built-in list action before this one,
// and reading the host's source cannot settle whether the surface is active there. The second is
// that a run started from the bar is a background job and nothing else: no dialog, no native alert,
// and an entry in the host's own job area.
//
// WHERE THE CATALOGUE COMES FROM. The tab's page read is answered at the network with a recorded
// page, so a range selection and a select-all count mean the same thing on every run. Intercepting
// the extension's own route keeps that seam inside this file; the shipped bundle carries no test
// hook. The host, the extension bundle, the tab and the bulk route are the real ones, and the run
// this spec starts is enqueued by the real server.
//
// WHAT THIS SPEC DOES NOT ASSERT. Cove's own pagination controls carry no focus utility of any
// kind, so no case here focuses one and reads a ring off it. That is the host's to change, and a red
// run against it would say nothing about this extension.
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
  EXTENSION_ID,
  expect,
  seedCoveStudio,
  STASHDB_ENDPOINT,
  WHISPARR_ROOT,
  WHISPARR_SYNC_EXTENSION,
} from "../lib/whisparr-sync-fixtures.mjs";

const TAB_LABEL = "Missing";

const HOST_JOBS = "/api/jobs";
const HOST_JOB_HISTORY = "/api/jobs/history";

/** How this extension types the run a selection starts, transcribed from the job's own constant. */
const BULK_JOB_TYPE = `ext:${EXTENSION_ID}:missing-bulk`;

// Transcribed by hand from the shipped copy and the design contract's control table. A spec
// importing the constants would be asserting that a string equals itself.
const SELECT_SCENE = "Select scene";
const SELECT_ALL = "Select all";
const SELECT_NONE = "Select none";
const INVERT_SELECTION = "Invert selection";
const MONITOR = "Monitor";
const REPORTS_IN_THE_JOB_DRAWER = "This runs in the background";
const NO_INSTANCE_CONNECTED = "No Whisparr instance is connected";

/** How many scenes the recorded page carries. One page's worth, which is what the route accepts. */
const SCENES_ON_THE_PAGE = 12;

const BUNDLE_BUDGET_MS = 60_000;
const BUNDLE_ATTEMPTS = 3;
const TAB_BUDGET_MS = 30_000;
const REGION_BUDGET_MS = 90_000;

const test = base.extend({
  selectionHarness: [
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
  baseUrl: async ({ selectionHarness }, use) => {
    await use(selectionHarness.baseUrl);
  },

  whisparrV3: [
    async ({ selectionHarness }, use) => {
      const whisparr = await startWhisparr({
        network: selectionHarness.container.getNetworkNames()[0],
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

/**
 * The bar, reached through the count it always draws while anything is ticked.
 *
 * The deepest element carrying that count, which is the bar itself: every ancestor of the count
 * carries it too, and the page's own wrappers would otherwise answer for the bar.
 */
const selectionBar = (page) =>
  page
    .locator("div")
    .filter({ has: page.getByText(/^\d+ selected$/) })
    .last();

/**
 * What the bar reports as ticked, or null while it draws nothing.
 *
 * Read in one pass in the page rather than as a count followed by a text read: the bar unmounts the
 * moment the selection empties, and the two-step read raises on the element it just found.
 */
async function selectedCount(page) {
  return page.evaluate(() => {
    for (const element of document.querySelectorAll("span")) {
      const stated = /^(\d+) selected$/.exec((element.textContent ?? "").trim());
      if (stated) return Number(stated[1]);
    }
    return null;
  });
}

/** One recorded page of `count` scenes, answered in place of the provider read. */
function recordedPage(count, { page: pageNumber = 1, lastPage = 1 } = {}) {
  const sceneList = [];
  for (let index = 0; index < count; index++) {
    sceneList.push({
      providerSceneId: `${String(pageNumber)}-${String(index)}-${randomUUID().slice(0, 8)}`,
      title: `Page ${String(pageNumber)} scene ${String(index)}`,
      releaseDate: "2019-04-02",
      coverUrl: null,
      studioName: "A studio",
      description: null,
      performers: [],
      tags: [],
      performerCount: 0,
      tagCount: 0,
      state: "notAdded",
    });
  }
  return {
    cards: sceneList,
    catalogueSize: count * lastPage,
    sizeIsLowerBound: false,
    page: pageNumber,
    perPage: 40,
    lastPage,
    rangeFrom: (pageNumber - 1) * count + 1,
    rangeTo: pageNumber * count,
    refusal: "none",
    facets: [],
    sorts: [],
    sortInForce: null,
    statusWasRead: true,
    statusIsPermanentlyAbsent: false,
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

test("missing selection: ticking a page, its shortcuts, and the run a press starts", async ({
  page,
  baseUrl,
  selectionHarness,
  whisparrV3,
}) => {
  // A container pair, an extension install and a browser, well above the shared per-test budget.
  test.setTimeout(900_000);

  const coveApi = createApiClient(
    () => selectionHarness.baseUrl,
    () => selectionHarness.token,
  );
  await connectWhisparr(coveApi, whisparrV3, "v3");

  const studio = await seedCoveStudio(coveApi, {
    name: `Selection studio ${randomUUID().slice(0, 8)}`,
    remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: randomUUID() }],
  });

  // A blocking dialog and a native alert are both absences this spec asserts, so both are watched
  // from the moment the page opens rather than at the one press that could raise them.
  const nativeDialogs = [];
  page.on("dialog", (dialog) => {
    nativeDialogs.push(dialog.type());
    void dialog.dismiss();
  });

  await page.route(/\/missing\?/, async (route) => {
    const asked = new URL(route.request().url()).searchParams.get("page") ?? "1";
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify(recordedPage(SCENES_ON_THE_PAGE, { page: Number(asked), lastPage: 2 })),
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
  await expect(cards(page)).toHaveCount(SCENES_ON_THE_PAGE);

  // CASE 3, taken first because it is the one assertion that has to run with no pointer interaction
  // in the grid before it. Cove's own list shortcuts reach a component an extension mounted inside a
  // detail page.
  await page.locator("body").click({ position: { x: 2, y: 2 } });
  await page.keyboard.press("s");
  await page.keyboard.press("a");
  await expect
    .poll(async () => selectedCount(page), {
      timeout: REGION_BUDGET_MS,
      message:
        "the select-all sequence Cove registers for its own list reached nothing on this tab, so the grid's shortcuts are unreachable from the keyboard",
    })
    .toBe(SCENES_ON_THE_PAGE);

  await page.keyboard.press("s");
  await page.keyboard.press("i");
  await expect
    .poll(async () => selectedCount(page), {
      message: "the invert sequence left the whole page ticked",
    })
    .toBeNull();

  await page.keyboard.press("s");
  await page.keyboard.press("a");
  await page.keyboard.press("s");
  await page.keyboard.press("n");
  await expect
    .poll(async () => selectedCount(page), { message: "the select-none sequence kept the ticks" })
    .toBeNull();

  // CASE 1. A pointer tick reads in the singular at one and in the plural at two.
  await cards(page).nth(0).getByRole("button", { name: SELECT_SCENE }).click();
  await expect(page.getByText("1 selected", { exact: true })).toBeVisible();
  await cards(page).nth(1).getByRole("button", { name: SELECT_SCENE }).click();
  await expect(page.getByText("2 selected", { exact: true })).toBeVisible();

  // CASE 2. Shift-clicking a card selects the range between it and the last one ticked.
  await cards(page)
    .nth(5)
    .getByRole("button", { name: SELECT_SCENE })
    .click({ modifiers: ["Shift"] });
  // Cards one to five, which is the range from the anchor, plus card zero, which was ticked before
  // the anchor moved and is outside the range.
  expect(
    await selectedCount(page),
    "a shift-click ticked one card rather than the range between it and the anchor",
  ).toBe(6);

  // CASE 8. No control anywhere in the bar acts on the whole result set.
  const barText = await selectionBar(page).innerText();
  expect(
    barText.toLowerCase(),
    "the bar offers a gesture over the whole result set, which is 1,600 provider requests against a large tag",
  ).not.toContain("matching");
  await expect(selectionBar(page).getByRole("button", { name: SELECT_ALL })).toBeVisible();
  await expect(selectionBar(page).getByRole("button", { name: SELECT_NONE })).toBeVisible();
  await expect(selectionBar(page).getByRole("button", { name: INVERT_SELECTION })).toBeVisible();

  // CASE 7. Every control in the bar shows a keyboard user where it is. Read as a computed box
  // shadow, because a class in the markup says nothing about whether the host declares it.
  for (const name of [SELECT_ALL, SELECT_NONE, INVERT_SELECTION, MONITOR]) {
    const control = selectionBar(page).getByRole("button", { name }).first();
    await control.focus();
    const shadow = await control.evaluate((element) => window.getComputedStyle(element).boxShadow);
    expect(shadow, `${name} paints no focus ring at all`).not.toBe("none");
  }

  // CASE 6. A run refused before it starts states the reason in a live region, keeps the ticks, and
  // leaves focus where it was, so the reader can fix the named cause and press again.
  const before = await selectedCount(page);
  await page.route(/\/missing\/bulk-monitor$/, async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ jobId: null, refusal: "noInstanceConnected" }),
    });
  });

  const monitorSelection = selectionBar(page).getByRole("button", { name: MONITOR }).first();
  await monitorSelection.focus();
  await monitorSelection.click();

  const status = page.getByRole("status").filter({ hasText: NO_INSTANCE_CONNECTED });
  await expect(status, "a refused run said nothing at all").toBeVisible({
    timeout: REGION_BUDGET_MS,
  });
  expect(await selectedCount(page), "a refused run threw the selection away").toBe(before);
  expect(
    await page.evaluate(() => document.activeElement?.textContent?.trim() ?? ""),
    "focus moved off the button, so pressing again after fixing the cause needs the pointer",
  ).toContain(MONITOR);

  await page.unroute(/\/missing\/bulk-monitor$/);

  // CASE 5. The real route enqueues the ticked scenes. The bar says where the result will appear
  // before the run starts, the ticks clear once it has, and nothing blocks the page at any point.
  await expect(page.getByText(REPORTS_IN_THE_JOB_DRAWER, { exact: false }).first()).toBeVisible();

  const enqueued = page.waitForResponse(
    (response) => /\/missing\/bulk-monitor$/.test(response.url()) && response.status() === 200,
  );
  await selectionBar(page).getByRole("button", { name: MONITOR }).first().click();
  const answered = await (await enqueued).json();
  expect(
    answered.jobId,
    `the run was refused rather than started: ${JSON.stringify(answered)}`,
  ).toBeTruthy();

  await expect
    .poll(async () => selectedCount(page), {
      timeout: REGION_BUDGET_MS,
      message: "a started run left the ticks behind, inviting a second run over the same scenes",
    })
    .toBeNull();

  await expect(
    page.getByRole("dialog"),
    "a run that reports in the job drawer opened a blocking dialog as well",
  ).toHaveCount(0);
  expect(nativeDialogs, "a native alert was raised").toEqual([]);

  // Both the running list and the history, because a run over a recorded page of scenes the instance
  // holds nothing for finishes fast enough to have moved between them.
  const listed = await Promise.all([coveApi.get(HOST_JOBS), coveApi.get(HOST_JOB_HISTORY)]).then(
    (answers) => answers.flatMap((answer) => (Array.isArray(answer.json) ? answer.json : [])),
  );
  expect(
    listed.some((job) => String(job.id ?? "") === String(answered.jobId)),
    `the host's job area holds no entry for the run that was started: ${JSON.stringify(listed.map((job) => [job.id, job.type])).slice(0, 400)}`,
  ).toBe(true);
  expect(
    listed.find((job) => String(job.id ?? "") === String(answered.jobId))?.type,
    "the run was enqueued under a type that is not this extension's own",
  ).toBe(BULK_JOB_TYPE);

  // CASE 4. Changing page clears the selection, so a tick always means a scene currently on screen.
  await cards(page).nth(0).getByRole("button", { name: SELECT_SCENE }).click();
  expect(await selectedCount(page)).toBe(1);

  await page.getByRole("button", { name: "Next page" }).first().click();
  await expect
    .poll(async () => selectedCount(page), {
      timeout: REGION_BUDGET_MS,
      message: "the selection survived a page change, so a tick no longer means a scene on screen",
    })
    .toBeNull();

  await page.unroute(/\/missing\?/);
});

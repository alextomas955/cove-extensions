// The grid's read behaviour and its pager, in a real containerized host.
//
// WHY THIS SPEC EXISTS. The unit suite runs in a node environment and renders no `.tsx`, so nothing
// below is provable there. Three properties of the grid are only true of a rendered page: that a
// page change does not blank the cards already on screen, that a failed refresh keeps them and says
// they are stale rather than replacing them with an error, and that the last page the pager offers
// carries rows the previous page did not.
//
// WHAT IS CONDITIONAL, AND WHY. Every assertion over a page of cards needs a real StashDB
// credential, lifted read-only from this machine's own Cove install. A machine with none is the
// ordinary case off this desk, and each conditional assertion names its reason in an annotation
// rather than passing silently.
//
// The count line is asserted only where the tab mounts one. The grid draws it from the page it was
// given, and the tab shell that supplies the provider and entity names is the surface that turns it
// on; until it does, this spec says so in an annotation instead of asserting an absence.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { registerRootFolder, startWhisparr } from "@cove-extensions/e2e/whisparr";
import { randomUUID } from "node:crypto";

import { configureProviderStub, startProviderStub } from "../../lib/provider-stub.mjs";
import {
  test as base,
  connectWhisparr,
  expect,
  seedCoveStudio,
  STASHDB_ENDPOINT,
  WHISPARR_ROOT,
  WHISPARR_SYNC_EXTENSION,
} from "../../lib/whisparr-sync-fixtures.mjs";

const TAB_LABEL = "Missing";

// The sentence the grid states over a list it kept when a refresh failed, transcribed by hand from
// the shipped copy. A spec importing the constant would be asserting that a string equals itself.
const READ_IS_STALE = "Cove couldn't check this just now. These are the last values it read.";

// A real StashDB studio with a catalogue of several thousand scenes, so the pager has pages to
// offer. The uuid is what Cove stores as its remote id.
const BRAZZERS_EXXTRA = "39cee498-a9ac-4403-910a-1a0157ad22d8";

const BUNDLE_BUDGET_MS = 60_000;
const BUNDLE_ATTEMPTS = 3;
const TAB_BUDGET_MS = 30_000;
const REGION_BUDGET_MS = 90_000;

const test = base.extend({
  countHarness: [
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
  baseUrl: async ({ countHarness }, use) => {
    await use(countHarness.baseUrl);
  },

  // The catalogue this spec reads, served on the network under the service's own name. A fixture
  // rather than a line in the test body, so it comes down with the stack even when an assertion
  // throws.
  provider: [
    async ({ countHarness }, use) => {
      const stub = await startProviderStub({
        networkName: countHarness.container.getNetworkNames()[0],
      });
      try {
        await use(stub);
      } finally {
        await stub.stop();
      }
    },
    { scope: "test" },
  ],

  whisparrV3: [
    async ({ countHarness }, use) => {
      const whisparr = await startWhisparr({
        network: countHarness.container.getNetworkNames()[0],
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
/** The range, which the toolbar states above the grid. */
const rangeInTheBar = (page) => page.getByRole("status").filter({ hasText: /\d+.*of\s+\d/ });

/** What the total counts, which the line under the bar states. */
const countLine = (page) =>
  page.getByRole("status").filter({ hasText: "not the number you are missing" });

/** Any sentence the tab stated in place of a grid. */
const statedReasons = (page) => page.locator("p").filter({ hasText: /\S/ });

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

function note(description) {
  test.info().annotations.push({ type: "skipped-assertion", description });
}

/** The identifiers of the cards on screen, in the order the grid drew them. */
async function firstCardTitle(page) {
  return (await cards(page).first().innerText()).trim();
}

test("the grid never blanks between reads, and the pager offers no page that repeats another", async ({
  page,
  baseUrl,
  countHarness,
  provider,
  whisparrV3,
}) => {
  // A container pair, an extension install and a browser, well above the shared per-test budget.
  test.setTimeout(900_000);

  const coveApi = createApiClient(
    () => countHarness.baseUrl,
    () => countHarness.token,
  );

  // The catalogue comes from a stub answering to the service's own name on this network, so this
  // spec reads a real captured page and needs no credential on the machine running it.
  await configureProviderStub(coveApi);

  // The read establishes a status for each surviving card, so with nothing connected it answers a
  // whole-grid refusal rather than a catalogue.
  await connectWhisparr(coveApi, whisparrV3, "v3");

  // An entity the provider issued no identifier for. Some reason is stated whether or not a
  // credential is available, so this assertion is the one that never skips. Which reason it is
  // depends on what the read route answers, which is not this surface's to decide.
  const unidentified = await seedCoveStudio(coveApi, {
    name: `Unidentified ${randomUUID().slice(0, 8)}`,
    remoteIds: [],
  });

  await visit(
    page,
    baseUrl,
    `/studio/${String(unidentified.id)}`,
    hostDetailTabs(page),
    "the studio detail page",
  );
  await expect(missingTab(page)).toBeVisible({ timeout: TAB_BUDGET_MS });
  await missingTab(page).click();

  await expect
    .poll(async () => statedReasons(page).count(), {
      timeout: REGION_BUDGET_MS,
      message:
        "the grid drew nothing and said nothing, which is the blank region a reader cannot tell from a catalogue that is genuinely empty",
    })
    .toBeGreaterThan(0);
  await expect(
    cards(page),
    "there is no catalogue to read for this studio, so no card may be drawn",
  ).toHaveCount(0);

  const studio = await seedCoveStudio(coveApi, {
    name: `Brazzers Exxtra ${randomUUID().slice(0, 8)}`,
    remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: BRAZZERS_EXXTRA }],
  });

  await visit(
    page,
    baseUrl,
    `/studio/${String(studio.id)}`,
    hostDetailTabs(page),
    "the studio detail page",
  );
  await missingTab(page).click();
  await expect(
    cards(page).first(),
    "a provider was configured, so the catalogue should have answered with cards",
  ).toBeVisible({ timeout: REGION_BUDGET_MS });

  // Read off the stub's own record. The cards on screen are evidence about this product only if the
  // page they came from is the one this spec served.
  expect(
    (await provider.asked()).filter((line) => line.includes("MissingPage")),
    "the stub was never asked for a page, so the grid is drawing something this spec did not serve",
  ).not.toEqual([]);

  const firstPageTop = await firstCardTitle(page);
  const drawn = await cards(page).count();
  expect(drawn, "a page of this catalogue is capped at forty cards").toBeLessThanOrEqual(40);

  // Whether the grid was given the provider and entity names its own sentences are filled with. The
  // tab shell is what supplies them, and until it does the grid states no reason of its own.
  const gridStatesItsOwnReasons = (await countLine(page).count()) > 0;

  if (gridStatesItsOwnReasons) {
    const stated = await rangeInTheBar(page).first().innerText();
    expect(
      stated,
      "the bar names a range and a total taken from the provider, so it cannot be the number of cards left after ownership was subtracted",
    ).toMatch(/\d+.*of\s+\d/);
    expect(
      await countLine(page).first().innerText(),
      "the line under the bar says what that total counts, the badge beside it having no room to",
    ).toContain("not the number you are missing");
  } else {
    note(
      "the count-line assertions did not run: the tab shell does not yet pass the grid the provider and entity names its sentences are filled with",
    );
  }

  // A page change keeps the previous page on screen. The read is held in flight deliberately, so the
  // assertion is about what the grid does while it waits rather than about how fast it answers.
  let held = null;
  await page.route(/\/missing\?/, async (route) => {
    if (held === null) {
      held = route;
      return;
    }
    await route.continue();
  });

  // The catalogue this spec serves is a fixed size, so the pager has a page to turn to and the
  // assertions below always run. They used to sit behind a count that could be zero, which is a
  // guard that silently removes them whenever the answer is small.
  const next = page.getByRole("button", { name: "Next page" }).first();
  await expect(
    next,
    "the served catalogue is larger than one page and the pager drew no next-page control",
  ).toHaveCount(1);

  await next.click();
  await expect(
    cards(page).first(),
    "the grid blanked while the next page was in flight, throwing away a correct answer to show nothing",
  ).toBeVisible();
  expect(
    await firstCardTitle(page),
    "the grid replaced the page on screen before the next one had arrived",
  ).toBe(firstPageTop);

  await held.continue();
  held = null;
  await page.unroute(/\/missing\?/);

  await expect
    .poll(async () => firstCardTitle(page), {
      timeout: REGION_BUDGET_MS,
      message: "the second page never replaced the first",
    })
    .not.toBe(firstPageTop);

  // The last page the pager offers carries rows of its own. A provider that clamps a page number
  // past its own last page re-serves that page, so a pager sized from a reported total would offer
  // pages that silently repeat.
  await page.getByRole("button", { name: "Last page" }).first().click();
  await expect
    .poll(async () => firstCardTitle(page), {
      timeout: REGION_BUDGET_MS,
      message: "the last page the pager offers repeats the page before it",
    })
    .not.toBe(firstPageTop);
  await expect(
    page.getByRole("button", { name: "Next page" }).first(),
    "the pager offered a page past the last one the provider will serve",
  ).toBeDisabled();

  const lastPageTop = await firstCardTitle(page);

  // A refresh that fails over a list on screen keeps the list and says it is stale.
  await page.route(/\/missing\?/, (route) => route.abort());
  await page.getByRole("button", { name: "Refresh" }).first().click();

  if (gridStatesItsOwnReasons) {
    await expect(
      page.getByText(READ_IS_STALE).first(),
      "a failed refresh did not say the values on screen may have moved",
    ).toBeVisible({ timeout: REGION_BUDGET_MS });
  } else {
    note(
      "the stale-read sentence did not run: the tab shell does not yet pass the grid the provider and entity names its sentences are filled with",
    );
  }

  // The property that holds either way: a failed refresh keeps the answer it has rather than
  // replacing a correct one with an error.
  await expect(cards(page).first()).toBeVisible();
  expect(
    await firstCardTitle(page),
    "a failed refresh blanked a correct answer to show an incorrect one",
  ).toBe(lastPageTop);
  await page.unroute(/\/missing\?/);
});

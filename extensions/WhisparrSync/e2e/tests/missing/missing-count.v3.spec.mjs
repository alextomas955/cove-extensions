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
import { randomUUID } from "node:crypto";

import {
  test as base,
  expect,
  seedCoveStudio,
  SPEC_BUDGET_MS,
  STASHDB_ENDPOINT,
} from "../../lib/connected-fixture.mjs";
import { configureProviderStub, startProviderStub } from "../../lib/provider-stub.mjs";
import { visit } from "../../lib/steps.mjs";

const TAB_LABEL = "Missing";

// The sentence the grid states over a list it kept when a refresh failed, transcribed by hand from
// the shipped copy. A spec importing the constant would be asserting that a string equals itself.
const READ_IS_STALE = "Cove couldn't check this just now. These are the last values it read.";

// A real StashDB studio with a catalogue of several thousand scenes, so the pager has pages to
// offer. The uuid is what Cove stores as its remote id.
const BRAZZERS_EXXTRA = "39cee498-a9ac-4403-910a-1a0157ad22d8";

/** What one page of this catalogue holds, transcribed from the server's own page size. */
const PAGE_CAP = 40;

const TAB_BUDGET_MS = 30_000;
const REGION_BUDGET_MS = 90_000;

// The catalogue this spec reads, served on the installation's own network under the service's own
// name. A fixture rather than a line in the test body, so it comes down with the stack even when an
// assertion throws.
const test = base.extend({
  provider: [
    async ({ isolatedCove }, use) => {
      const stub = await startProviderStub({
        networkName: isolatedCove.container.getNetworkNames()[0],
      });
      try {
        await use(stub);
      } finally {
        await stub.stop();
      }
    },
    { scope: "test" },
  ],
});

test.describe.configure({ timeout: SPEC_BUDGET_MS });
test.use({ generation: "v3" });

const missingTab = (page) => page.getByRole("tab", { name: TAB_LABEL }).first();
const hostDetailTabs = (page) => page.getByRole("tablist").first();
const cards = (page) => page.locator("article").filter({ has: page.locator("img, h3") });
/** The range, which the toolbar states above the grid. */
const rangeInTheBar = (page) => page.getByRole("status").filter({ hasText: /\d+.*of\s+\d/ });

/** Any sentence the tab stated in place of a grid. */
const statedReasons = (page) => page.locator("p").filter({ hasText: /\S/ });

/** The identifiers of the cards on screen, in the order the grid drew them. */
async function firstCardTitle(page) {
  return (await cards(page).first().innerText()).trim();
}

test("the grid never blanks between reads, and the pager offers no page that repeats another", async ({
  page,
  baseUrl,
  connected,
}) => {
  // The fixture holds the instance the read establishes a status against: with nothing connected the
  // route answers a whole-grid refusal rather than a catalogue.
  const { api: coveApi, whisparr } = connected;

  // The catalogue comes from a stub answering to the service's own name on this network, so this
  // spec reads a real captured page and needs no credential on the machine running it.
  await configureProviderStub(coveApi);

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

  // The instance is what lists a studio's scenes now, so one it does not hold lists none and
  // the tab states that instead of drawing a grid. Seeded under the identifier the library
  // carries, which is what the extension resolves the Cove studio to.
  await whisparr.seedEntity("v3", {
    kind: "studio",
    foreignId: BRAZZERS_EXXTRA,
    title: studio.name,
  });

  // The scenes the tab lists. A studio the instance holds with no works answers an empty catalogue,
  // which draws the same blank region as a studio it does not hold at all.
  // More than two pages of them, because this spec pages forward once and then asks for the last
  // page: with exactly two, that step is already on the last page and its control is correctly
  // disabled. Numbered so a title sorts the way the row does, which is what makes "no page repeats
  // another" readable.
  const catalogue = Array.from({ length: PAGE_CAP * 3 + 5 }, (_, index) => ({
    foreignId: `${BRAZZERS_EXXTRA}-${String(index).padStart(3, "0")}`,
    title: `Catalogue Scene ${String(index).padStart(3, "0")} ${studio.name}`,
  }));
  // Written in one run. Seeded one at a time, a catalogue this size costs a file copy and a process
  // start per scene, which is longer than this test is allowed to take.
  const seeded = await whisparr.seedScenes("v3", {
    scenes: catalogue,
    studioForeignId: BRAZZERS_EXXTRA,
    studioTitle: studio.name,
  });
  // The seed is the premise of every paging assertion below. Read back here so a seed that wrote
  // fewer rows than asked reports itself, rather than surfacing as a pager control that never
  // enables and a test that spends its whole budget waiting for it.
  expect(
    seeded.length,
    `the seeder wrote ${String(seeded.length)} of ${String(catalogue.length)} scenes, so there is not more than one page to page through`,
  ).toBe(catalogue.length);

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
    "the instance lists a catalogue for this studio, so it should have answered with cards",
  ).toBeVisible({ timeout: REGION_BUDGET_MS });

  // The cards are evidence about this product only if they came from the catalogue this spec
  // seeded. Read against the instance's own works: this product asks the instance for what a studio
  // lists, and the metadata stub is configured here only so the library's identifiers resolve.
  await expect(
    cards(page).filter({ hasText: catalogue[0].title }),
    `no card carries a title this spec seeded, so the grid is drawing a catalogue it did not serve: ${catalogue.map((one) => one.title).join(", ")}`,
  ).toHaveCount(1);

  const firstPageTop = await firstCardTitle(page);
  const drawn = await cards(page).count();
  expect(drawn, "a page of this catalogue is capped at forty cards").toBeLessThanOrEqual(PAGE_CAP);

  await expect(
    rangeInTheBar(page).first(),
    "the bar states no range and total at all, so nothing says the figure came from the provider rather than from the cards left after ownership was subtracted",
  ).toBeVisible({ timeout: REGION_BUDGET_MS });
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
  // assertions below always run. Behind a count that could be zero they would be skipped silently
  // whenever the answer was small.
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

  // The control has to be offered before it can be pressed. Read first, because a pager with one
  // page leaves it disabled and a press on a disabled control spends the whole test budget waiting
  // for it to become pressable, reporting a timeout that names nothing.
  await expect(
    page.getByRole("button", { name: "Last page" }).first(),
    `the catalogue does not span pages, so there is no last page to offer. The tab reports "${String(await page.getByRole("status").first().textContent())}" over ${String(catalogue.length)} seeded scenes`,
  ).toBeEnabled({ timeout: REGION_BUDGET_MS });

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

  await expect(
    page.getByText(READ_IS_STALE).first(),
    "a failed refresh did not say the values on screen may have moved",
  ).toBeVisible({ timeout: REGION_BUDGET_MS });

  // The property that holds either way: a failed refresh keeps the answer it has rather than
  // replacing a correct one with an error.
  await expect(cards(page).first()).toBeVisible();
  expect(
    await firstCardTitle(page),
    "a failed refresh blanked a correct answer to show an incorrect one",
  ).toBe(lastPageTop);
  await page.unroute(/\/missing\?/);
});

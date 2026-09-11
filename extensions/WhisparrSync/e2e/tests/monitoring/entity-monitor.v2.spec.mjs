// The entity control on a real studio page with v2 connected.
//
// WHY THIS SPEC EXISTS. The sibling specs for this generation drive monitoring through the
// extension's own route, so nothing else in this suite presses the control against it. Three facts
// are only observable in a browser on a v2 connection:
//
// - Whether the host renders the slot component at all when the connected generation is this one.
// - Which mark the control draws. Each generation has its own, and the component picks by the
//   generation the read answered with, so a wrong pick shows the other product's logo.
// - Which rows the menu offers. Two of this product's three secondary actions are held by this
//   generation and one is not, and the menu states that per row rather than hiding it.
//
// The one-way-door sentence is the sharpest of them. This generation applies a scope change
// retroactively and the newer one does not, so the confirmation in front of the wider scope carries
// a sentence here that it must not carry there.
//
// NO SEARCH IS EXECUTED HERE. The search row is read and never pressed, and the wider scope is
// cancelled rather than confirmed, so the instance's catalogue is left as the fixture seeded it.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
//
// `baseUrl` is overridden onto the isolated host. The `page` fixture resolves its address through
// `baseUrl`, so without the override the browser would drive the worker-shared instance while every
// instance assertion addressed the isolated one.
import { pollUntil } from "@cove-extensions/e2e/poll";

import { visit } from "../../lib/steps.mjs";
import { expect, siteRow, SPEC_BUDGET_MS, test as v2Test } from "../../lib/v2-fixture.mjs";

// Transcribed by hand from the extension's own copy module, never imported: a spec reading the same
// constant the component renders would be asserting that a string equals itself.
const WHISPARR_NOT_MONITORED = "Whisparr, not monitored";
const WHISPARR_MONITORED = "Whisparr, monitored";
const SCOPE_FUTURE_SCENES = "Monitor - new releases only";
const SCOPE_ALL_SCENES = "Monitor - all scenes (queue back-catalogue)";
const UNMONITOR = "Unmonitor";
const ACTION_ADD_ALL_MISSING = "Add all missing";
const ACTION_REFLECT_OWNED = "Reflect owned";
const ACTION_SEARCH_ALL_MONITORED = "Search all monitored";
const CAP_UNAVAILABLE_ON_THIS_GENERATION = "Currently available on Whisparr v3 (Eros)";
const ALL_SCENES_MARKS_THE_BACK_CATALOGUE =
  "Monitoring all scenes marks every scene Whisparr already lists for this entity as wanted, which spends indexer traffic and disk.";
const ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE =
  "Narrowing the scope back to new releases only does not undo this: a scene that is already wanted stays wanted.";

// The disc this generation's mark is drawn on, in the fill the component ships. The other
// generation's disc is a different colour, so the fill is what says which product was drawn.
const V2_MARK_DISC_FILL = "#ff69b4";

// Each budget names the operation it bounds, so a failure says which one blew it rather than
// reporting the whole test as a timeout naming nothing.
const CONTROL_BUDGET_MS = 60_000;
const GESTURE_BUDGET_MS = 60_000;

const test = v2Test.extend({
  baseUrl: async ({ isolatedHarness }, use) => {
    await use(isolatedHarness.baseUrl);
  },
});

test.describe.configure({ timeout: SPEC_BUDGET_MS });

/** The extension's control, by the only name it has, in whichever state it is in. */
const anyMonitorControl = (page) =>
  page
    .getByRole("button", { name: new RegExp(`^(${WHISPARR_NOT_MONITORED}|${WHISPARR_MONITORED})`) })
    .first();

/** Cove's own primary action on the detail page, which the slot has to render to the left of. */
const hostEditButton = (page) => page.getByRole("button", { name: "Edit", exact: true });

test("the control renders on a v2 connection, draws this generation's mark, and both directions reach the instance", async ({
  page,
  baseUrl,
  v2,
}) => {
  const { whisparrApi, studio, seeded } = v2;

  const control = anyMonitorControl(page);
  await visit(page, baseUrl, `/studio/${String(studio.id)}`, control, "the studio detail page");

  await expect(
    control,
    `the host rendered no control for this extension's action-row slot within ${String(CONTROL_BUDGET_MS)}ms. ` +
      "Either the released host image carries no such slot, or the manifest's componentName does not resolve to a key in this bundle's component map, which renders nothing and reports nothing.",
  ).toBeVisible({ timeout: CONTROL_BUDGET_MS });

  const edit = hostEditButton(page);
  await expect(edit, "Cove's own Edit button is not on this page").toBeVisible();
  const controlBox = await control.boundingBox();
  const editBox = await edit.boundingBox();
  expect(
    controlBox.x,
    `the control is drawn at x=${String(controlBox.x)} and Cove's Edit at x=${String(editBox.x)}, so the slot is not at the head of the hero action row`,
  ).toBeLessThan(editBox.x);

  // The fixture seeds the site monitored, so this is also the evidence that the read reached the
  // instance rather than defaulting.
  await expect(
    control,
    "the control does not report the seeded site as monitored, so the read did not reach the instance",
  ).toHaveAttribute("aria-label", new RegExp(`^${WHISPARR_MONITORED}`), {
    timeout: CONTROL_BUDGET_MS,
  });

  // Which product was drawn. The component picks the mark by the generation the read answered with,
  // so the other generation's disc here is a read that named the wrong one.
  await expect(
    control.locator(`svg ellipse[fill="${V2_MARK_DISC_FILL}"]`),
    "the control drew the other generation's mark, so the read named a generation this instance is not",
  ).toBeVisible();

  await control.click();
  const menu = page.getByRole("menu", { name: WHISPARR_MONITORED });
  await expect(menu, "the control opened no menu").toBeVisible({ timeout: CONTROL_BUDGET_MS });

  // The capability gate, rendered. This generation holds two of the three secondary actions, and the
  // third states why it is not offered rather than being absent from the menu.
  await expect(
    menu.getByRole("menuitem", { name: ACTION_ADD_ALL_MISSING }),
    "the row behind a capability this generation does not hold is offered without its reason",
  ).toHaveAttribute("title", `${ACTION_ADD_ALL_MISSING}, ${CAP_UNAVAILABLE_ON_THIS_GENERATION}`);
  for (const held of [ACTION_REFLECT_OWNED, ACTION_SEARCH_ALL_MONITORED]) {
    await expect(
      menu.getByRole("menuitem", { name: held }),
      `${held} is behind a capability this generation holds, so the row carries no reason`,
    ).toHaveAttribute("title", held);
  }

  // THE ONE-WAY-DOOR SENTENCE. A scope change is retroactive on this generation, so the wider scope
  // can be taken back and the confirmation must not say otherwise. Cancelled rather than confirmed:
  // what is under test is the sentence, and confirming would mark the seeded catalogue wanted.
  await menu.getByRole("menuitemradio", { name: SCOPE_ALL_SCENES, exact: true }).click();
  const confirm = page.getByRole("dialog", { name: SCOPE_ALL_SCENES });
  await expect(confirm, "the wider scope asked for no confirmation").toBeVisible({
    timeout: CONTROL_BUDGET_MS,
  });
  await expect(
    confirm.getByText(ALL_SCENES_MARKS_THE_BACK_CATALOGUE, { exact: false }),
    "the confirmation does not say what the wider scope costs",
  ).toBeVisible();
  await expect(
    confirm.getByText(ALL_SCENES_IS_NOT_UNDONE_BY_A_LATER_SCOPE_CHANGE, { exact: false }),
    "the confirmation calls the wider scope a one-way door, which is the other generation's behaviour: this one applies a scope change retroactively",
  ).toHaveCount(0);
  await confirm.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(confirm, "cancelling left the confirmation on screen").toHaveCount(0);

  // BOTH DIRECTIONS, read off the instance's own row rather than this product's answer. A route that
  // answered 200 and wrote nothing would pass an assertion made against its answer.
  await control.click();
  await page
    .getByRole("menu", { name: WHISPARR_MONITORED })
    .getByRole("menuitem", { name: UNMONITOR, exact: true })
    .click();
  await pollUntil(
    async () => (await siteRow(whisparrApi, seeded.seriesId))?.monitored,
    (monitored) => monitored === false,
    {
      timeoutMs: GESTURE_BUDGET_MS,
      label: "the instance's own row after the unmonitor gesture",
    },
  );

  await expect(
    page.getByRole("button", { name: new RegExp(`^${WHISPARR_NOT_MONITORED}`) }).first(),
    "the control still reports the entity as monitored after the instance stopped monitoring it",
  ).toBeVisible({ timeout: GESTURE_BUDGET_MS });

  // The menu stays open across an action and redraws in the state that was read back, so the way
  // back is a row of the menu already on screen. Pressing the control here would close it.
  const reopened = page.getByRole("menu", { name: WHISPARR_NOT_MONITORED });
  await expect(
    reopened,
    "the menu closed on the gesture, so a reader who wants the other scope has to find the control again",
  ).toBeVisible({ timeout: GESTURE_BUDGET_MS });
  await reopened.getByRole("menuitemradio", { name: SCOPE_FUTURE_SCENES, exact: true }).click();
  await pollUntil(
    async () => (await siteRow(whisparrApi, seeded.seriesId))?.monitored,
    (monitored) => monitored === true,
    {
      timeoutMs: GESTURE_BUDGET_MS,
      label: "the instance's own row after the monitor gesture",
    },
  );
});

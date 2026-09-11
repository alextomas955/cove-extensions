// What the entity control draws and offers on a v2 connection, and nothing it shares with the other
// generation.
//
// The monitor and unmonitor gestures are one scenario collected on both generations, in
// entity-monitor.shared.spec.mjs. What is left here is observable only on this connection:
//
// - Which mark the control draws. Each generation has its own, and the component picks by the
//   generation the read answered with, so a wrong pick shows the other product's logo.
// - Which rows the menu offers. Two of this product's three secondary actions are held by this
//   generation and one is not, and the menu states that per row rather than hiding it.
// - The one-way-door sentence, which is the sharpest of them. This generation applies a scope change
//   retroactively and the other does not, so the confirmation in front of the wider scope carries a
//   sentence here that it must not carry there.
//
// NO SEARCH IS EXECUTED HERE. The search row is read and never pressed, and the wider scope is
// cancelled rather than confirmed, so the instance's catalogue is left as the fixture seeded it.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { pollUntil } from "@cove-extensions/e2e/poll";

import { expect, SPEC_BUDGET_MS, test } from "../../lib/connected-fixture.mjs";
import { visit } from "../../lib/steps.mjs";

// Transcribed by hand from the extension's own copy module, never imported: a spec reading the same
// constant the component renders would be asserting that a string equals itself.
const WHISPARR_NOT_MONITORED = "Whisparr, not monitored";
const WHISPARR_MONITORED = "Whisparr, monitored";
const SCOPE_FUTURE_SCENES = "Monitor - new releases only";
const SCOPE_ALL_SCENES = "Monitor - all scenes (queue back-catalogue)";
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

test.use({ generation: "v2" });

test.describe.configure({ timeout: SPEC_BUDGET_MS });

/** The extension's control, by the only name it has, in whichever state it is in. */
const anyMonitorControl = (page) =>
  page
    .getByRole("button", { name: new RegExp(`^(${WHISPARR_NOT_MONITORED}|${WHISPARR_MONITORED})`) })
    .first();

/** Cove's own primary action on the detail page, which the slot has to render to the left of. */
const hostEditButton = (page) => page.getByRole("button", { name: "Edit", exact: true });

test("the control draws this generation's mark, states its capability gaps per row, and calls the wider scope reversible", async ({
  page,
  baseUrl,
  connected,
}) => {
  const { studio, studioMonitored } = connected;

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

  // Which product was drawn. The component picks the mark by the generation the read answered with,
  // so the other generation's disc here is a read that named the wrong one.
  await expect(
    control.locator(`svg ellipse[fill="${V2_MARK_DISC_FILL}"]`),
    "the control drew the other generation's mark, so the read named a generation this instance is not",
  ).toBeVisible({ timeout: CONTROL_BUDGET_MS });

  // THE ONE-WAY-DOOR SENTENCE. A scope change is retroactive on this generation, so the wider scope
  // can be taken back and the confirmation must not say otherwise. Cancelled rather than confirmed:
  // what is under test is the sentence, and confirming would mark the seeded catalogue wanted.
  await control.click();
  await page
    .getByRole("menu", { name: WHISPARR_NOT_MONITORED })
    .getByRole("menuitemradio", { name: SCOPE_ALL_SCENES, exact: true })
    .click();
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

  // The secondary rows are offered only once the entity is monitored, so the narrower scope is taken
  // first. It queues no back-catalogue, which is what makes it the one to reach them through.
  await control.click();
  await page
    .getByRole("menu", { name: WHISPARR_NOT_MONITORED })
    .getByRole("menuitemradio", { name: SCOPE_FUTURE_SCENES, exact: true })
    .click();
  await pollUntil(studioMonitored, (monitored) => monitored === true, {
    timeoutMs: GESTURE_BUDGET_MS,
    label: "the instance's own row after the monitor gesture",
  });

  // THE CAPABILITY GATE, RENDERED. This generation holds two of the three secondary actions, and the
  // third states why it is not offered rather than being absent from the menu.
  const menu = page.getByRole("menu", { name: WHISPARR_MONITORED });
  await expect(menu, "the menu did not follow the state the instance now reports").toBeVisible({
    timeout: GESTURE_BUDGET_MS,
  });
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
});

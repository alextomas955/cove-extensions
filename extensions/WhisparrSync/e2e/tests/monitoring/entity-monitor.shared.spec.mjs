// Monitoring a studio from the browser, on every generation that holds the control.
//
// One scenario, collected once per generation, each execution against an installation of its own.
// The generation is a fixture option rather than anything this body reads: the fixture starts the
// instance, seeds the catalogue and supplies the read, so the actions and the assertions below are
// the same words whichever generation is connected.
//
// WHAT THE EVIDENCE IS. The monitored flag is read off the instance's own row, never off this
// product's answer. A route that replied 200 and wrote nothing would pass an assertion made against
// its reply. Both directions are driven for the same reason: a route writing a constant would pass
// either one alone.
//
// WHY THE STUDIO IS SEEDED IN COVE THROUGH THE FIXTURE'S OWN CLIENT. Finding that studio in the
// browser is what says the client and the browser address one installation. A browser left on
// another Cove reads another database and renders no such studio.
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
const UNMONITOR = "Unmonitor";

// Each budget names the operation it bounds, so a failure says which one blew it rather than
// reporting the whole test as a timeout naming nothing.
const CONTROL_BUDGET_MS = 60_000;
const GESTURE_BUDGET_MS = 60_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

/** The extension's control, by the only name it has, in whichever state it is in. */
const anyMonitorControl = (page) =>
  page
    .getByRole("button", { name: new RegExp(`^(${WHISPARR_NOT_MONITORED}|${WHISPARR_MONITORED})`) })
    .first();

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    // Only this generation's installation and instance, so a block pays for one of each.
    test.use({ generation });

    test("a studio is monitored and unmonitored from its detail page, and the instance follows both", async ({
      page,
      baseUrl,
      connected,
    }) => {
      const { studio, studioName, studioMonitored } = connected;

      const control = anyMonitorControl(page);
      await visit(page, baseUrl, `/studio/${String(studio.id)}`, control, "the studio detail page");

      await expect(
        page.getByText(studioName, { exact: false }).first(),
        `the browser is on a studio page that does not show ${studioName}, so it is reading a different installation from the client that seeded it`,
      ).toBeVisible({ timeout: CONTROL_BUDGET_MS });

      await expect(
        control,
        `the host rendered no control for this extension's action-row slot within ${String(CONTROL_BUDGET_MS)}ms. ` +
          "Either the released host image carries no such slot, or the manifest's componentName does not resolve to a key in this bundle's component map, which renders nothing and reports nothing.",
      ).toBeVisible({ timeout: CONTROL_BUDGET_MS });

      // The fixture seeds the studio unmonitored, so this is also the evidence that the read reached
      // the instance rather than defaulting to the state a control draws before it has read anything.
      await expect(
        control,
        "the control does not report the seeded studio as unmonitored, so the read did not reach the instance",
      ).toHaveAttribute("aria-label", new RegExp(`^${WHISPARR_NOT_MONITORED}`), {
        timeout: CONTROL_BUDGET_MS,
      });

      await control.click();
      const menu = page.getByRole("menu", { name: WHISPARR_NOT_MONITORED });
      await expect(menu, "the control opened no menu").toBeVisible({ timeout: CONTROL_BUDGET_MS });

      // The narrower scope, which asks for no confirmation and marks no back-catalogue wanted. What
      // is under test is the flag reaching the instance, and the wider scope would spend indexer
      // traffic to assert the same thing.
      await menu.getByRole("menuitemradio", { name: SCOPE_FUTURE_SCENES, exact: true }).click();
      await pollUntil(studioMonitored, (monitored) => monitored === true, {
        timeoutMs: GESTURE_BUDGET_MS,
        label: "the instance's own row after the monitor gesture",
      });

      await expect(
        page.getByRole("button", { name: new RegExp(`^${WHISPARR_MONITORED}`) }).first(),
        "the instance reports the studio as monitored and the control did not follow it",
      ).toBeVisible({ timeout: GESTURE_BUDGET_MS });

      // The menu stays open across an action and redraws in the state that was read back, so the way
      // back is a row of the menu already on screen. Pressing the control here would close it.
      const reopened = page.getByRole("menu", { name: WHISPARR_MONITORED });
      await expect(
        reopened,
        "the menu closed on the gesture, so a reader who wants the other direction has to find the control again",
      ).toBeVisible({ timeout: GESTURE_BUDGET_MS });
      await reopened.getByRole("menuitem", { name: UNMONITOR, exact: true }).click();
      await pollUntil(studioMonitored, (monitored) => monitored === false, {
        timeoutMs: GESTURE_BUDGET_MS,
        label: "the instance's own row after the unmonitor gesture",
      });

      await expect(
        page.getByRole("button", { name: new RegExp(`^${WHISPARR_NOT_MONITORED}`) }).first(),
        "the control still reports the studio as monitored after the instance stopped monitoring it",
      ).toBeVisible({ timeout: GESTURE_BUDGET_MS });
    });
  });
}

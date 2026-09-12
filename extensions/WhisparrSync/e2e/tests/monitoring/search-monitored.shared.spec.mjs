// Asking the instance to search what an entity monitors, from the menu, on every generation that
// holds the verb.
//
// One scenario, collected once per generation, each execution against an installation of its own.
// The generation is a fixture option rather than anything this body reads: the fixture starts the
// instance, seeds the catalogue and supplies the reads, so the gesture and the assertions below are
// the same words whichever generation is connected.
//
// THIS IS THE ONE GESTURE ON THIS SURFACE THAT DOWNLOADS, and it is safe to press because the
// instance is asserted to hold no indexer and no download client before anything happens. A search
// it starts then has nowhere to search and nothing to hand a transfer to. Its queue is read
// afterwards for the other half of that claim.
//
// WHAT THE EVIDENCE IS. The instance's own command roster, never this product's answer. The route
// answers a monitoring view rather than a job, so its answer says nothing about whether the request
// arrived, and a route that wrote nothing would pass an assertion made against its reply. The roster
// is never expected to be empty - the instance runs scheduled tasks of its own - so the bound is
// taken first and what is asserted is a searching command that was not there before.
//
// THE SENTENCES ARE IMPORTED FROM THE SHIPPED COPY MODULE, because here they are LOCATORS: which
// row to press. A locator built from a hand-copied literal stops finding its row the day the row is
// renamed, silently. Its sibling entity-monitor.shared.spec.mjs transcribes instead, because there
// the sentence is what is asserted, and reading the constant the component renders would assert that
// a string equals itself.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { attemptUntil, pollUntil } from "@cove-extensions/e2e/poll";

import {
  ACTION_SEARCH_ALL_MONITORED,
  SCOPE_FUTURE_SCENES,
  WHISPARR_MONITORED,
  WHISPARR_NOT_MONITORED,
} from "../../../src/WhisparrSync.Ui/src/common/ui/copy.ts";
import {
  expect,
  SETTLE_DWELL_MS,
  SPEC_BUDGET_MS,
  test,
  whisparrAcquisitionSurface,
} from "../../lib/connected-fixture.mjs";
import { visit } from "../../lib/steps.mjs";

// A command whose name carries this is the observable form of a started search on both generations.
// A pattern rather than the command names, so this file names no verb that downloads.
const SEARCH_COMMAND = /search/i;

// Each budget names the operation it bounds, so a failure says which one blew it rather than
// reporting the whole test as a timeout naming nothing.
const CONTROL_BUDGET_MS = 60_000;
const GESTURE_BUDGET_MS = 60_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

const notMonitoredControl = (page) =>
  page.getByRole("button", { name: new RegExp(`^${WHISPARR_NOT_MONITORED}`) }).first();

const monitoredMenu = (page) => page.getByRole("menu", { name: WHISPARR_MONITORED });

/** Every command the instance has been asked to run whose name says it searches. */
const searching = (roster) => roster.filter((name) => SEARCH_COMMAND.test(name));

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    // Only this generation's installation and instance, so a block pays for one of each.
    test.use({ generation });

    test("the menu asks the instance to search what the entity monitors, and the instance records it", async ({
      page,
      baseUrl,
      connected,
    }) => {
      const { adapter, instance, studio, studioMonitored } = connected;

      // The bound on every never-acquired claim below, read off the instance rather than assumed. A
      // fixture that grew an indexer would make this press acquisitive and nothing here would notice.
      expect(
        await whisparrAcquisitionSurface(instance),
        "the fixture instance has an indexer or a download client, so a search started here could acquire something and this press may not be made",
      ).toEqual({ indexers: 0, downloadClients: 0 });

      // The other bound. The instance runs commands of its own accord, so a searching command
      // already on the roster would make the one read after the press meaningless.
      const before = await adapter.activity(instance);
      expect(
        searching(before.commandNames),
        `the instance had already been asked to search before anything was pressed. Its whole roster was ${JSON.stringify(before.commandNames)}`,
      ).toEqual([]);

      const control = notMonitoredControl(page);
      await visit(page, baseUrl, `/studio/${String(studio.id)}`, control, "the studio detail page");
      await expect(
        control,
        `the control does not report the seeded studio as unmonitored within ${String(CONTROL_BUDGET_MS)}ms, so the read did not reach the instance`,
      ).toBeVisible({ timeout: CONTROL_BUDGET_MS });

      // The verb under test is offered only on an entity the instance already monitors, so the
      // monitoring gesture comes first. The narrower scope, which marks no back-catalogue wanted:
      // the wider one would spend the instance's own traffic to arrange the same state.
      await control.click();
      const menu = page.getByRole("menu", { name: WHISPARR_NOT_MONITORED });
      await expect(menu, "the control opened no menu").toBeVisible({ timeout: CONTROL_BUDGET_MS });
      await menu.getByRole("menuitemradio", { name: SCOPE_FUTURE_SCENES, exact: true }).click();
      await pollUntil(studioMonitored, (monitored) => monitored === true, {
        timeoutMs: GESTURE_BUDGET_MS,
        label: "the instance's own row after the monitor gesture",
      });

      // The menu stays open across an action and redraws in the state that was read back, so the row
      // this spec is about is reached from the menu already on screen.
      await expect(
        monitoredMenu(page),
        "the menu closed on the monitor gesture, so the rows a monitored entity offers are out of reach",
      ).toBeVisible({ timeout: GESTURE_BUDGET_MS });
      const row = monitoredMenu(page).getByRole("menuitem", {
        name: ACTION_SEARCH_ALL_MONITORED,
        exact: true,
      });
      await expect(
        row,
        `the monitored studio's menu carries no "${ACTION_SEARCH_ALL_MONITORED}" row, so this generation offers no way to ask for the search it declares it can start`,
      ).toBeVisible({ timeout: GESTURE_BUDGET_MS });
      await expect(
        row,
        `the "${ACTION_SEARCH_ALL_MONITORED}" row is not pressable on a monitored studio, so the route it names is not reachable from the menu that offers it`,
      ).toBeEnabled();
      await row.click();

      const {
        settled: asked,
        value: roster,
        note,
      } = await attemptUntil(
        async (_signal, record) => {
          const { commandNames } = await adapter.activity(instance);
          record(`${String(searching(commandNames).length)} searching command(s)`);
          return searching(commandNames).length > 0 ? { value: commandNames } : null;
        },
        {
          timeoutMs: GESTURE_BUDGET_MS,
          intervalMs: 1_000,
          label: "the instance records a searching command",
        },
      );
      expect(
        asked,
        `the instance was never asked to search after the row was pressed; its roster last read ${note}`,
      ).toBe(true);

      // The other half, and the reason this press is safe to make at all: the instance has nowhere
      // to search and nothing to hand a transfer to, so being asked starts none. Watched over the
      // named window rather than read the moment the poll returned: an absence bounded by whatever
      // delay the run happened to have passes on a broken instance as readily as on a correct one.
      await page.waitForTimeout(SETTLE_DWELL_MS);
      const after = await adapter.activity(instance);
      expect(
        after.queueTotal,
        `the instance's queue holds ${String(after.queueTotal)} record(s) after the search it was asked for, so a press this spec makes can acquire. Its roster was ${JSON.stringify(roster)}`,
      ).toBe(0);
    });
  });
}

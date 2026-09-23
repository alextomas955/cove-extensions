// The performers selection bar on a v2 connection, in a real host.
//
// v2 has no performer entity, so this product registers nothing for performers on that connection:
// no card badge, no toolbar toggle, no list row, no detail action and no bulk button. This spec is
// the evidence that the bulk button is absent rather than present and explaining itself.
//
// WHY AN ABSENCE IS WORTH A SPEC. The bulk action is registered in the manifest, and a manifest
// entry that slipped outside the generation gate would put a control for a kind this connection
// cannot address back in front of a reader, with nothing in the build to notice. The host draws the
// selection bar either way, so the bar is waited for first and the button read inside it: a bar that
// never rendered would otherwise report as a button that is correctly gone.
//
// WHAT IT DOES NOT COVER. Whether pressing such a button would change anything on the instance. There
// is no button to press. The instance's rows are read anyway, so a build that reached it through some
// other path is not recorded here as a clean pass.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
import { randomUUID } from "node:crypto";

import {
  expect,
  seedCovePerformer,
  SETTLE_DWELL_MS,
  SPEC_BUDGET_MS,
  test,
  THEPORNDB_ENDPOINT,
} from "../../lib/connected-fixture.mjs";
import { visit } from "../../lib/steps.mjs";

// Transcribed by hand from the extension's own registration, never imported. A spec importing the
// constant the product declares would be asserting that a string equals itself.
const BULK_ACTION_LABEL = "Whisparr";

const SEEDED_PERFORMERS = 2;

const PAGE_BUDGET_MS = 60_000;
const SELECTION_BAR_BUDGET_MS = 60_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });
test.use({ generation: "v2" });

const cardToggles = (page) => page.getByRole("button", { name: "Select item" });

const bulkButton = (page) => page.getByRole("button", { name: BULK_ACTION_LABEL, exact: true });

/**
 * The host's own count of what is selected, which it draws whatever any extension registered.
 *
 * Read instead of the bar itself: every locator for the bar in this suite filters on a control an
 * extension put there, and this spec exists because there is no such control to filter on.
 */
const selectedCount = (page) => page.getByText(/^\d+ selected$/);

test("v2 puts no Whisparr control on the performers selection bar", async ({
  page,
  baseUrl,
  connected,
}) => {
  const { api: coveApi, instance, run } = connected;

  for (let index = 0; index < SEEDED_PERFORMERS; index++) {
    await seedCovePerformer(coveApi, {
      name: `Bulk Performer ${String(index)} ${run}`,
      remoteIds: [{ endpoint: THEPORNDB_ENDPOINT, remoteId: randomUUID() }],
    });
  }

  const seriesBefore = await instance.get("/api/v3/series");
  expect(
    seriesBefore.status,
    `GET the instance's own rows answered ${String(seriesBefore.status)}`,
  ).toBe(200);

  await visit(page, baseUrl, "/performers", cardToggles(page).first(), "the performers page");
  const toggles = cardToggles(page);
  await expect(toggles, "the performers page does not hold the cards this spec seeded").toHaveCount(
    SEEDED_PERFORMERS,
    { timeout: PAGE_BUDGET_MS },
  );
  for (let index = 0; index < SEEDED_PERFORMERS; index++) {
    await toggles.nth(index).click();
  }

  // The bar first. Read before it renders, an absent button says only that the page had not caught
  // up, which passes on a build that registers the action it should not.
  await expect(
    selectedCount(page),
    "the host drew no selection bar, so what it carries cannot be read",
  ).toBeVisible({ timeout: SELECTION_BAR_BUDGET_MS });

  // Watched over the named window the rest of this folder uses. A control that mounts late is
  // otherwise indistinguishable from one that never mounts.
  await page.waitForTimeout(SETTLE_DWELL_MS);
  await expect(
    bulkButton(page),
    `the performers selection bar carries a "${BULK_ACTION_LABEL}" button, so a control for a kind this connection cannot address is in front of a reader`,
  ).toHaveCount(0);

  // Nothing reached the instance by another path while the selection stood.
  const seriesAfter = await instance.get("/api/v3/series");
  expect(
    seriesAfter.status,
    `GET the instance's own rows answered ${String(seriesAfter.status)} after the selection, so what they now say is unread`,
  ).toBe(200);
  expect(
    (seriesAfter.json ?? []).filter((row) => row.monitored).map((row) => row.id),
    "selecting performers on a connection that holds none changed what the instance monitors",
  ).toEqual((seriesBefore.json ?? []).filter((row) => row.monitored).map((row) => row.id));
});

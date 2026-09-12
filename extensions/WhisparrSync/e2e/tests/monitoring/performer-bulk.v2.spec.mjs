// The bulk button on the performers selection bar when the connected generation holds no performer
// at all, in a real host.
//
// THIS IS NOT A TEST OF PERFORMER MONITORING. v2 has no performer entity, so there is nothing here
// to monitor and no assertion below claims anything was. What is under test is the button that
// appears anyway: the action is registered unconditionally, unlike the performer slots beside it,
// so on this connection a reader can press a control for a kind the connection cannot address. A control in that position is meant to say why, and this spec is the evidence that it
// does and that pressing it changes nothing the instance holds.
//
// WHY THE BUTTON IS THERE AT ALL. The card badges, the toolbar toggle and the list row are all
// registered behind the generation gate and are genuinely absent here. The two bulk actions are
// registered outside it, which is what makes this surface reachable and this spec necessary.
//
// WHY NOTHING-CHANGED IS READ OFF THE INSTANCE. A handler that sent a request the server refused
// would look the same from the browser as one that sent nothing. The instance's own rows are the
// only place the difference is visible.
//
// WHAT NOTHING-CHANGED MEANS HERE, EXACTLY. What the instance monitors, and whether it was asked to
// search. Not its whole state: it runs scheduled commands of its own and its rows carry sync
// timestamps that move with them, so a comparison of the whole roster or the whole row goes red on
// the instance's own work rather than on anything this page did. The claim is that the panel changed
// nothing it could have changed, which is a reading of the catalogue and says nothing about whether
// a request left the browser.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
import { randomUUID } from "node:crypto";

import {
  expect,
  searching,
  seedCovePerformer,
  SETTLE_DWELL_MS,
  SPEC_BUDGET_MS,
  test,
  THEPORNDB_ENDPOINT,
  whisparrActivity,
} from "../../lib/connected-fixture.mjs";
import { visit } from "../../lib/steps.mjs";

// Transcribed by hand from the extension's own registration and copy module, never imported. A spec
// importing the constants the product declares would be asserting that a string equals itself.
const BULK_ACTION_LABEL = "Whisparr";
const BULK_CLOSE = "Close";
const CAPABILITY_ABSENT = "Currently available on Whisparr v3 (Eros)";

const SEEDED_PERFORMERS = 2;

// Each budget names the operation it bounds, so a failure says which one blew it rather than
// reporting the whole test as a timeout naming nothing.
const PAGE_BUDGET_MS = 60_000;
const BULK_BUTTON_BUDGET_MS = 60_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });
test.use({ generation: "v2" });

const bulkButton = (page) => page.getByRole("button", { name: BULK_ACTION_LABEL, exact: true });

/** The panel the button opens, which heads itself with the product's name and the selected count. */
const chooserPanel = (page) =>
  page.getByRole("menu", { name: `Whisparr · ${String(SEEDED_PERFORMERS)} selected` });

/**
 * Every card's own selection toggle, in DOM order.
 *
 * @see entity-monitor-bulk.v3.spec.mjs — anchored on the pair because Playwright's `name` option is
 * a substring match, and the plain string walks back onto an already-selected card.
 */
const cardToggles = (page) => page.getByRole("button", { name: /^(Select|Deselect) item$/ });

/**
 * What each of the instance's own rows is monitored as.
 *
 * The monitored flag alone, because that is what the verb the panel offers would change. The rest of
 * a row carries the instance's own sync timestamps, which move on their own.
 */
const monitoredRows = (rows) =>
  rows.map((row) => ({ id: row.id, monitored: row.monitored })).sort((a, b) => a.id - b.id);

test("the performers bulk button states why v2 cannot address that kind, and changes nothing the instance holds", async ({
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

  const before = await whisparrActivity(instance);
  const askedToSearch = new Set(searching(before.commandNames));
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

  // The button is registered outside the generation gate, so it appears on a connection that holds
  // no performer. Asserted explicitly: a missing button and a press that went elsewhere are the same
  // observation once anything below is inferred from a click.
  await expect(
    bulkButton(page),
    `the performers selection bar carries no "${BULK_ACTION_LABEL}" button within ${String(BULK_BUTTON_BUDGET_MS)}ms, so the action is no longer registered on a connection of this generation`,
  ).toBeVisible({ timeout: BULK_BUTTON_BUDGET_MS });

  await bulkButton(page).click();
  await expect(
    chooserPanel(page),
    "the bulk button opened no panel, so the reader is told nothing at all",
  ).toBeVisible();

  // The words the surface actually uses, and nothing offered beside them.
  await expect(
    chooserPanel(page).getByText(CAPABILITY_ABSENT, { exact: true }),
    "the panel opened over a kind this connection cannot address and stated no reason for it",
  ).toBeVisible();
  const rows = await chooserPanel(page).getByRole("menuitem").allInnerTexts();
  expect(
    rows.map((row) => row.trim()),
    "the panel offers a verb over a kind the connected generation holds none of",
  ).toEqual([BULK_CLOSE]);

  await chooserPanel(page).getByRole("menuitem", { name: BULK_CLOSE, exact: true }).click();
  await expect(chooserPanel(page), "leaving the panel did not close it").toBeHidden();

  // Watched over the named window the rest of this folder uses rather than read the moment the panel
  // closed: an absence bounded by an accident passes on a broken build as readily as on a correct
  // one.
  await page.waitForTimeout(SETTLE_DWELL_MS);

  const after = await whisparrActivity(instance);
  expect(
    searching(after.commandNames).filter((name) => !askedToSearch.has(name)),
    `a panel that offered nothing asked the instance to search. Its whole roster was ${JSON.stringify(after.commandNames)}, and before the panel opened it was ${JSON.stringify(before.commandNames)}`,
  ).toEqual([]);
  expect(
    after.queueTotal,
    `the instance's queue total moved from ${String(before.queueTotal)} to ${String(after.queueTotal)} across a panel that offered nothing`,
  ).toBe(before.queueTotal);
  const seriesAfter = await instance.get("/api/v3/series");
  expect(
    seriesAfter.status,
    `GET the instance's own rows answered ${String(seriesAfter.status)} after the panel, so what they now say is unread`,
  ).toBe(200);
  expect(
    monitoredRows(seriesAfter.json),
    "a panel that offered nothing changed what the instance monitors",
  ).toEqual(monitoredRows(seriesBefore.json));
});

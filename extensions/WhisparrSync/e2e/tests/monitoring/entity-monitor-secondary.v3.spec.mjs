// The verb only the newer generation holds, and the rows a monitored entity offers, driven from a
// real studio page against a real instance.
//
// WHY THIS FILE IS GENERATION-SPECIFIC. Registering the missing scenes of a studio is a capability
// the product's own table gives to this generation alone, so there is no same gesture on the older
// one to pair it with. Its two former neighbours - handing the instance a file the library owns, and
// asking it to search what an entity monitors - are held by both, and each is now one shared
// scenario pressed on both generations.
//
// WHAT IS LEFT HERE THAT NO OTHER TIER REACHES:
//
// - Whether the three rows exist at all on a monitored entity, and whether they are ABSENT rather
//   than disabled on one nothing monitors. Three routes were mounted for them, and mounting a route
//   is exactly the change that could make a row appear where the product says none should.
// - Whether each of those routes reached the shipped wire document, which is where a row's
//   reachability is a fact about what shipped rather than about what one page rendered.
// - What the registering verb does to the instance: it registers, and registering is not acquiring.
//
// THE SENTENCES ARE IMPORTED FROM THE SHIPPED COPY MODULE, because here they are LOCATORS: which row
// to press, which row must be absent. A locator built from a hand-copied literal stops finding its
// row the day the row is renamed, silently.
//
// WHAT THIS SPEC DELIBERATELY DOES NOT COVER, AND WHY IT IS NOT AN OMISSION. The sentence an action
// that never arrived states is reached from `state.actionError`, which is set only when the
// EXTENSION's own route answers non-2xx. An unreachable Whisparr is not that case: the route reaches
// it, fails to, and answers a refusal with a 200, which the browser states its own sentence for. So
// no address this spec could point the extension at produces that notice, and simulating a 500 to
// see it would assert a condition rather than observe one. The notice's own geometry is asserted in
// reflect-owned.shared.spec.mjs, on the same portal path and on a notice a real gesture produced.
//
// IF THIS SPEC GOES RED, read the log for a container-not-running line before debugging the UI. A red
// e2e in this repository is usually the Cove container dying rather than the page under test.
import { attemptUntil } from "@cove-extensions/e2e/poll";
import { readFileSync } from "node:fs";
import { randomUUID } from "node:crypto";
import { join } from "node:path";

import {
  ACTION_ADD_ALL_MISSING,
  ACTION_REFLECT_OWNED,
  ACTION_SEARCH_ALL_MONITORED,
  WHISPARR_MONITORED,
  WHISPARR_NOT_MONITORED,
} from "../../../src/WhisparrSync.Ui/src/common/ui/copy.ts";
import {
  expect,
  extensionRoute,
  seedCoveVideo,
  SETTLE_DWELL_MS,
  SPEC_BUDGET_MS,
  STASHDB_ENDPOINT,
  test,
  whisparrAcquisitionSurface,
  whisparrActivity,
} from "../../lib/connected-fixture.mjs";
import { visit } from "../../lib/steps.mjs";

/** The emitted wire document, which is where a mounted route is reachable from without a request. */
const WIRE_DOCUMENT = join(import.meta.dirname, "..", "..", "..", "wire", "openapi.json");

// The three rows a monitored entity offers, and the routes behind them.
const SECONDARY_ROWS = [ACTION_ADD_ALL_MISSING, ACTION_REFLECT_OWNED, ACTION_SEARCH_ALL_MONITORED];
const SECONDARY_ROUTES = ["reflect-owned", "add-all-missing", "search-all-monitored"];

// The narrower scope, in the wire spelling the server binds it in - not the label the menu draws.
const SCOPE_FUTURE_SCENES_WIRE = "futureScenes";

const CONTROL_BUDGET_MS = 60_000;
const GESTURE_BUDGET_MS = 60_000;
const JOB_BUDGET_MS = 120_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

const hostEditButton = (page) => page.getByRole("button", { name: "Edit", exact: true });

const monitoredControl = (page) =>
  page.getByRole("button", { name: new RegExp(`^${WHISPARR_MONITORED}`) }).first();

const monitoredMenu = (page) => page.getByRole("menu", { name: WHISPARR_MONITORED });

/** Follows one enqueued run to a settled state through the extension's own status route. */
async function followJob(api, jobId, label) {
  const { settled, value, note } = await attemptUntil(
    async (_signal, record) => {
      const status = await api.get(extensionRoute(`job-status/${String(jobId)}`));
      record(`${String(status.status)} with state ${String(status.json?.status ?? "absent")}`);
      return status.json?.status === "completed" ? { value: status.json } : null;
    },
    { timeoutMs: JOB_BUDGET_MS, intervalMs: 1_000, label },
  );
  expect(
    settled,
    `${label} never reported itself complete within ${String(JOB_BUDGET_MS)}ms; its status route last answered ${note}`,
  ).toBe(true);
  return value;
}

/**
 * The line a settled run reports what it did on, or undefined where it reported none.
 *
 * Read from whichever field of the status carries it. These runs put their line on the final
 * progress report's sub-task, because the host's progress carries no summary field of its own.
 */
function reportedLine(status, shape) {
  return [status.subTask, status.summary].find(
    (line) => typeof line === "string" && shape.test(line),
  );
}

/**
 * How many scenes the instance holds in its own catalogue.
 *
 * Read from the resource this generation keeps a scene as, and refused when the route does not
 * answer with a list. A reader that took a refusal as an empty catalogue would compare nothing with
 * nothing and report that the catalogue did not shrink whatever the verb did.
 */
async function catalogueCount(instance) {
  const route = "/api/v3/movie";
  const listed = await instance.get(route);
  if (!Array.isArray(listed.json)) {
    throw new Error(
      `catalogueCount: ${route} answered ${String(listed.status)} with no list: ${String(listed.text).slice(0, 300)}`,
    );
  }
  return listed.json.length;
}

test.use({ generation: "v3" });

test("the rows a monitored studio offers, and the verb that registers what the library holds and the instance does not", async ({
  page,
  baseUrl,
  connected,
}) => {
  const { api, instance, run, studio } = connected;

  // The bound on every never-searched claim below, read off the instance rather than assumed: a
  // fixture that grew an indexer would make this verb acquisitive and no assertion here would
  // notice.
  expect(
    await whisparrAcquisitionSurface(instance),
    "the fixture instance has an indexer or a download client, so a transfer started here could acquire something and no never-searched claim may be taken against it",
  ).toEqual({ indexers: 0, downloadClients: 0 });

  // A scene the library holds under this studio, carrying an identity and no file. The identity is
  // what the registering verb offers the instance; a studio holding nothing would settle nothing.
  await seedCoveVideo(api, {
    title: `Cove E2E Secondary Scene ${run}`,
    studioId: studio.id,
    remoteIds: [
      { endpoint: STASHDB_ENDPOINT, remoteId: `cove-e2e-secondary-scene-${randomUUID()}` },
    ],
  });

  // ---- Nothing monitors the studio, so none of the three rows exists. ----
  await visit(
    page,
    baseUrl,
    `/studio/${String(studio.id)}`,
    hostEditButton(page),
    "the unmonitored studio's page",
  );
  const quietControl = page.getByRole("button", { name: new RegExp(`^${WHISPARR_NOT_MONITORED}`) });
  await expect(
    quietControl,
    `the unmonitored studio's control never became pressable within ${String(CONTROL_BUDGET_MS)}ms`,
  ).toBeEnabled({ timeout: CONTROL_BUDGET_MS });
  await quietControl.click();
  const quietMenu = page.getByRole("menu", { name: new RegExp(`^${WHISPARR_NOT_MONITORED}`) });
  await expect(quietMenu, "the unmonitored control opened no menu").toBeVisible();
  // Bounded by the named window for the reason every absence here is: a row the menu has not
  // rendered yet is indistinguishable from one it never will.
  await page.waitForTimeout(SETTLE_DWELL_MS);
  for (const label of SECONDARY_ROWS) {
    await expect(
      quietMenu.getByRole("menuitem", { name: label, exact: true }),
      `the unmonitored studio's menu carries a "${label}" row. The three secondary verbs must be ABSENT on an entity nothing monitors, not present and disabled: a disabled row advertises a verb that is not the reader's to ask for yet.`,
    ).toHaveCount(0);
  }
  await page.keyboard.press("Escape");
  await expect(quietMenu, "Escape did not close the unmonitored studio's menu").toBeHidden();

  // Arranged over the API rather than by the gesture its sibling specs already drive: what this spec
  // is about starts at the menu a monitored entity opens.
  const monitored = await api.post(extensionRoute(`entity/studio/${String(studio.id)}/monitor`), {
    scope: SCOPE_FUTURE_SCENES_WIRE,
  });
  expect(
    monitored.json?.monitored,
    `arranging the studio as monitored answered ${String(monitored.status)} ${JSON.stringify(monitored.json)}`,
  ).toBe(true);

  // ---- The three rows on the monitored studio. ----
  await visit(
    page,
    baseUrl,
    `/studio/${String(studio.id)}`,
    hostEditButton(page),
    "the monitored studio's page",
  );
  await expect(
    monitoredControl(page),
    `the control never reported the studio as monitored within ${String(CONTROL_BUDGET_MS)}ms`,
  ).toBeVisible({ timeout: CONTROL_BUDGET_MS });
  await monitoredControl(page).click();
  await expect(monitoredMenu(page), "the monitored control opened no menu").toBeVisible();

  for (const label of SECONDARY_ROWS) {
    const row = monitoredMenu(page).getByRole("menuitem", { name: label, exact: true });
    await expect(
      row,
      `the monitored studio's menu carries no "${label}" row, so this build mounted a route the menu does not offer`,
    ).toBeVisible();
    await expect(
      row,
      `the "${label}" row is disabled on a monitored studio, so the route it names is not reachable from the menu that offers it`,
    ).toBeEnabled();
  }

  // And the same routes in the emitted document, which is where their reachability is a fact about
  // what shipped rather than about what one page rendered.
  const raw = readFileSync(WIRE_DOCUMENT, "utf8");
  // Sliced rather than matched: a byte-order mark breaks JSON.parse, and a regex holding the mark
  // itself is an invisible character in the source.
  const wire = JSON.parse(raw.charCodeAt(0) === 0xfeff ? raw.slice(1) : raw);
  const mounted = Object.keys(wire.paths ?? {});
  for (const verb of SECONDARY_ROUTES) {
    expect(
      mounted.filter((path) => path.endsWith(`/${verb}`)),
      `the emitted wire document declares no route ending in "${verb}", so a row the menu offers is served by nothing`,
    ).toHaveLength(1);
  }

  // ---- The registering verb, on the generation that carries it. ----
  const catalogueBefore = await catalogueCount(instance);
  const enqueued = page.waitForResponse(
    (response) => new URL(response.url()).pathname.endsWith("/add-all-missing"),
    { timeout: GESTURE_BUDGET_MS },
  );
  await monitoredMenu(page)
    .getByRole("menuitem", { name: ACTION_ADD_ALL_MISSING, exact: true })
    .click();
  const response = await enqueued;
  const body = await response.json().catch(() => null);
  expect(
    body?.jobId,
    `the add-all-missing route answered ${String(response.status())} ${JSON.stringify(body)} with no job id`,
  ).toBeTruthy();

  const addAllRun = await followJob(api, body.jobId, "the add-all-missing run");
  expect(
    reportedLine(addAllRun, /\d+ registered, \d+ already held, \d+ refused|carries an identifier/),
    `the add-all-missing run completed and reported no line saying what it offered the instance. The whole status was ${JSON.stringify(addAllRun)}`,
  ).toBeTruthy();

  // The load-bearing half: registering a scene is not acquiring one. Whatever the instance did with
  // the offer, it queued no transfer.
  const after = await whisparrActivity(instance);
  expect(
    after.queueTotal,
    `the instance's queue holds ${String(after.queueTotal)} record(s) after the registering verb, so registering a scene started a transfer`,
  ).toBe(0);

  // What the instance chose to answer the offer with is recorded rather than asserted: a synthetic
  // identifier is one its own metadata source cannot resolve, and a measured build refuses exactly
  // that. So the catalogue is read for a DECREASE, which no outcome of this verb may produce, rather
  // than for a gain this fixture cannot honestly produce.
  expect(
    await catalogueCount(instance),
    "the instance's scene catalogue shrank across the registering verb, which registers and never removes",
  ).toBeGreaterThanOrEqual(catalogueBefore);
});

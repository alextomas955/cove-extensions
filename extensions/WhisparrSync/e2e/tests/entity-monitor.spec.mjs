// The entity control on both real detail pages, against the containerized host and a real Whisparr.
//
// Everything upstream of this spec is asserted against doubles and source text. Three facts cannot
// be reached that way, and this is where they are settled:
//
// - Whether the HOST renders the slot component in its hero action row at all. That is a fact about
//   a released host image this repository does not build.
// - Whether the control sits where this product says it does, which is the host's own ordering
//   rather than anything this bundle decides.
// - Whether a monitor gesture starts a search. That is a runtime fact about a real instance, and no
//   composed body can answer it.
//
// WHAT THE NEVER-SEARCHED OBSERVATION PROVES, AND WHAT IT DOES NOT. The fixture instance reports
// zero indexers and zero download clients, asserted below rather than assumed, so a search that DID
// start could have found nothing and fetched nothing. The observation is therefore the instance's
// own command roster and its queue total, which is the same evidence the fixture ledger uses. It is
// deliberately weaker than the composed-body assertions in the unit tier, and nothing stronger is
// claimed from it here. The roster is never expected to be EMPTY either: the instance runs scheduled
// tasks of its own and they appear in it, so what is asserted is the absence of a command whose name
// says it searches.
//
// NO SEARCH IS EXECUTED ANYWHERE IN THIS SPEC. The grabbing verb's correctness is asserted on its
// composed body in the unit tier and is deliberately never run.
//
// IF THIS SPEC GOES RED, read the job log for a container-not-running line before debugging the UI.
// A red e2e in this repository is usually the Cove container dying rather than the page under test.
//
// Its own Cove per test, and `baseUrl` is overridden onto it: the `page` fixture resolves its
// address through `baseUrl`, so without the override the browser would drive the WORKER-shared
// instance while every API assertion addressed the isolated one. This spec also saves the
// extension's connection settings, which are global to the install and would otherwise leak into
// every sibling spec sharing a worker.
import { test as base, createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { registerRootFolder, startWhisparr } from "@cove-extensions/e2e/whisparr";
import { attemptUntil } from "@cove-extensions/e2e/poll";
import { randomUUID } from "node:crypto";

import {
  connectWhisparr,
  expect,
  extensionRoute,
  seedCovePerformer,
  seedCoveStudio,
  SETTLE_DWELL_MS,
  STASHDB_ENDPOINT,
  whisparrAcquisitionSurface,
  whisparrActivity,
  whisparrEntity,
  WHISPARR_ROOT,
  WHISPARR_SYNC_EXTENSION,
} from "../lib/whisparr-sync-fixtures.mjs";

// The sentence the settings panel itself draws. It exists only inside the component this extension
// ships, so reaching it means the whole bundle loaded and the host resolved its component map. The
// extension's NAME would not do: the host draws that from the manifest alone.
const PANEL_SENTENCE =
  "The address Cove itself reaches Whisparr on, including the scheme and port.";
const SETTINGS_PATH = "/settings/whisparr-sync";

// Transcribed by hand from the extension's own copy module, never imported: a spec reading the same
// constant the component renders would be asserting that a string equals itself.
const MONITOR_IN_WHISPARR = "Monitor in Whisparr";
const MONITORED_IN_WHISPARR = "Monitored in Whisparr";
const NO_IDENTITY_IN_THIS_NAMESPACE =
  "Cove holds no link for this entity that the connected Whisparr can identify it by.";
const SCOPE_FUTURE_SCENES = "Future Scenes";
const SCOPE_ALL_SCENES = "All Scenes";
const STOP_MONITORING_IN_WHISPARR = "Stop monitoring in Whisparr";

// The refusal kind meaning nothing was refused, in the wire spelling the server answers it in.
const MONITOR_REFUSAL_NONE = "none";

// A command whose name carries this is the observable form of a started search on both generations.
// A pattern rather than the three command names, so this file names no verb that downloads and the
// source check over it stays at zero.
const SEARCH_COMMAND = /search/i;

// How many entities this spec puts in front of the host. Stated here and bounded at a handful: the
// library it drives is the one it created, so a count read off a page would be a count of whatever
// else happened to be there.
const SEEDED_STUDIOS = 2;
const SEEDED_PERFORMERS = 1;

// Each budget names the operation it bounds, so a failure says which one blew it rather than
// reporting the whole test as a timeout naming nothing.
const BUNDLE_BUDGET_MS = 60_000;
const BUNDLE_ATTEMPTS = 3;
const CONTROL_BUDGET_MS = 60_000;
const READ_SETTLED_BUDGET_MS = 60_000;
const GESTURE_BUDGET_MS = 60_000;

const test = base.extend({
  monitorHarness: [
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

  // Read through the handle AFTER the install. The install restarts the container, which re-mints
  // the token and can republish the instance on a different host port.
  baseUrl: async ({ monitorHarness }, use) => {
    await use(monitorHarness.baseUrl);
  },
});

/** The extension's control, by the only name it has, in whichever state it is in. */
const anyMonitorControl = (page) =>
  page
    .getByRole("button", { name: new RegExp(`^(${MONITOR_IN_WHISPARR}|${MONITORED_IN_WHISPARR})`) })
    .first();

const monitorControl = (page) =>
  page.getByRole("button", { name: new RegExp(`^${MONITOR_IN_WHISPARR}`) });

const monitoredControl = (page) =>
  page.getByRole("button", { name: new RegExp(`^${MONITORED_IN_WHISPARR}`) });

/** Cove's own primary action on either detail page, which the slot has to render to the left of. */
const hostEditButton = (page) => page.getByRole("button", { name: "Edit", exact: true });

/**
 * Opens `path`, re-navigating while nothing the caller named has rendered.
 *
 * The host carries an unknown settings key only until it finishes loading extensions, then rewrites
 * the address to its first built-in tab; and it paints its own error boundary in place of a page
 * whose lazily-imported chunk failed to fetch, on the correct URL and indefinitely. Only a fresh
 * navigation recovers either, and the retry is bounded so a permanent failure is not turned into a
 * hung test.
 */
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

/** Asserts the extension's control renders in the hero action row, ahead of Cove's own Edit. */
async function expectControlLeftOfEdit(page, where) {
  const control = anyMonitorControl(page);
  await expect(
    control,
    `${where}: the host rendered no control for this extension's action-row slot within ${CONTROL_BUDGET_MS}ms. ` +
      "Either the released host image carries no such slot, or the manifest's componentName does not resolve to a key in this bundle's component map — which renders nothing and reports nothing.",
  ).toBeVisible({ timeout: CONTROL_BUDGET_MS });

  const edit = hostEditButton(page);
  await expect(edit, `${where}: Cove's own Edit button is not on this page`).toBeVisible();

  // Document order is what "left of" means for a row the host lays out in source order, and the
  // rendered geometry is what a reader actually sees. Both, because either alone can be satisfied
  // while the other is not.
  const [controlHandle, editHandle] = await Promise.all([
    control.elementHandle(),
    edit.elementHandle(),
  ]);
  const controlPrecedesEdit = await page.evaluate(
    ([a, b]) => (a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING) !== 0,
    [controlHandle, editHandle],
  );
  expect(
    controlPrecedesEdit,
    `${where}: the extension's control does not precede Cove's Edit button in the document, so the slot did not render at the head of the hero action row`,
  ).toBe(true);

  const controlBox = await control.boundingBox();
  const editBox = await edit.boundingBox();
  expect(
    controlBox.x,
    `${where}: the control is drawn at x=${controlBox.x} and Cove's Edit at x=${editBox.x}, so it is not to the left of it on screen`,
  ).toBeLessThan(editBox.x);

  return control;
}

test("the control renders and works on both real detail pages, and the instance shows no search was started", async ({
  page,
  baseUrl,
  monitorHarness,
}) => {
  // Two container pairs, an extension install, a browser and a real instance. Well above the shared
  // per-test budget, and deliberately its own number rather than a raised default for every spec.
  test.setTimeout(900_000);

  const coveApi = createApiClient(
    () => monitorHarness.baseUrl,
    () => monitorHarness.token,
  );

  const whisparr = await startWhisparr({
    network: monitorHarness.container.getNetworkNames()[0],
    generations: ["v3"],
  });

  try {
    const instance = whisparr.apiFor("v3");
    whisparr.v3.rootFolder = await registerRootFolder(
      whisparr.v3.container,
      instance,
      "v3",
      WHISPARR_ROOT,
    );

    // The bound on every never-searched claim below, read off the instance rather than assumed.
    expect(
      await whisparrAcquisitionSurface(instance),
      "the fixture instance has an indexer or a download client, so a search started here could acquire something and no never-searched claim may be taken against it",
    ).toEqual({ indexers: 0, downloadClients: 0 });

    const run = randomUUID().slice(0, 8);
    const studioForeignId = `cove-e2e-studio-${run}`;
    const performerForeignId = `cove-e2e-performer-${run}`;

    const seededStudio = await whisparr.seedEntity("v3", {
      kind: "studio",
      foreignId: studioForeignId,
      title: `Cove E2E Studio ${run}`,
    });
    expect(
      seededStudio.monitored,
      "the seeded studio arrived already monitored, so a monitored reading after the gesture would prove nothing",
    ).toBe(false);

    await whisparr.seedEntity("v3", {
      kind: "performer",
      foreignId: performerForeignId,
      title: `Cove E2E Performer ${run}`,
    });

    const reachableStudio = await seedCoveStudio(coveApi, {
      name: `Reachable ${run}`,
      remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: studioForeignId }],
    });
    // D-08's ordinary case rather than a rare one: roughly a tenth of the owner's studios carry no
    // id in this generation's namespace, and this is the only place that is exercised in a real host.
    const unreachableStudio = await seedCoveStudio(coveApi, { name: `Unreachable ${run}` });
    const reachablePerformer = await seedCovePerformer(coveApi, {
      name: `Reachable Performer ${run}`,
      remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: performerForeignId }],
    });
    expect(
      [reachableStudio, unreachableStudio].length + [reachablePerformer].length,
      "this spec seeded a different number of entities than it declares, so its bound is not the one stated",
    ).toBe(SEEDED_STUDIOS + SEEDED_PERFORMERS);

    await connectWhisparr(coveApi, whisparr, "v3");

    // FIRST, and the order is the point. The host loads every extension's bundle under one promise,
    // so a bundle that throws takes down every extension surface on the page. A failure here means
    // nothing else below is meaningful, and the failure a reader must see is this one.
    await visit(
      page,
      baseUrl,
      SETTINGS_PATH,
      page.getByText(PANEL_SENTENCE, { exact: true }),
      "the whole-bundle load",
    );

    // The slot on the studio detail page, and where the host put it.
    await visit(
      page,
      baseUrl,
      `/studio/${String(reachableStudio.id)}`,
      hostEditButton(page),
      "the studio detail page",
    );
    await expectControlLeftOfEdit(page, "the studio detail page");

    // The same on the performer detail page: its own slot name, its own registration, its own
    // component.
    await visit(
      page,
      baseUrl,
      `/performer/${String(reachablePerformer.id)}`,
      hostEditButton(page),
      "the performer detail page",
    );
    await expectControlLeftOfEdit(page, "the performer detail page");

    // The unreachable studio: disabled, with its reason reachable both on hover and in the name.
    await visit(
      page,
      baseUrl,
      `/studio/${String(unreachableStudio.id)}`,
      hostEditButton(page),
      "the unreachable studio's detail page",
    );
    const refused = monitorControl(page);
    const spoken = `${MONITOR_IN_WHISPARR}, ${NO_IDENTITY_IN_THIS_NAMESPACE}`;
    await expect(
      refused,
      `the unreachable studio's control never named its reason within ${READ_SETTLED_BUDGET_MS}ms; the control carries no visible label, so its accessible name is the only name it has`,
    ).toHaveAccessibleName(spoken, { timeout: READ_SETTLED_BUDGET_MS });
    await expect(
      refused,
      "the reason is in the accessible name and not in the hover text, so a pointer and a screen reader are told different things",
    ).toHaveAttribute("title", spoken);
    await refused.hover();
    await expect(
      refused,
      "the control names a reason and is still pressable, so a reader would be sent to a menu the instance cannot honour",
    ).toBeDisabled();

    // Back to the reachable studio for the menu and the gesture.
    await visit(
      page,
      baseUrl,
      `/studio/${String(reachableStudio.id)}`,
      hostEditButton(page),
      "the studio detail page",
    );
    await expect(
      monitorControl(page),
      `the reachable studio's control never became pressable within ${READ_SETTLED_BUDGET_MS}ms`,
    ).toBeEnabled({ timeout: READ_SETTLED_BUDGET_MS });

    // The menu opens, the arrow keys move through it, and Escape closes it without the host page
    // reacting to the same key.
    const urlBeforeTheMenu = page.url();
    await monitorControl(page).click();
    const menu = page.getByRole("menu", { name: MONITOR_IN_WHISPARR });
    await expect(menu, "the control did not open its menu on a click").toBeVisible();

    const futureScenes = menu.getByRole("menuitemradio", { name: SCOPE_FUTURE_SCENES });
    const allScenes = menu.getByRole("menuitemradio", { name: SCOPE_ALL_SCENES });
    await expect(
      allScenes,
      "the menu offers no second scope, so the arrow keys have nothing to move between",
    ).toBeVisible();

    // The menu focuses its first row as it opens, so a reader who never touches the pointer is
    // already somewhere the menu can be read from.
    await expect(
      futureScenes,
      "the menu opened with focus nowhere, so the arrow keys have no origin and a reader is told nothing about where they are",
    ).toBeFocused();
    await page.keyboard.press("ArrowDown");
    await expect(
      allScenes,
      "ArrowDown did not move focus to the next row, so the menu's roving focus never found its rows",
    ).toBeFocused();
    await page.keyboard.press("ArrowUp");
    await expect(futureScenes, "ArrowUp did not move focus back to the previous row").toBeFocused();

    await page.keyboard.press("Escape");
    await expect(menu, "Escape did not close the menu").toBeHidden();
    expect(
      page.url(),
      "Escape closed the menu and the host page reacted to the same key, so it reached past the overlay",
    ).toBe(urlBeforeTheMenu);
    await expect(
      monitorControl(page),
      "Escape closed the menu and left focus on the document, so a reader is told nothing about where they now are",
    ).toBeFocused();

    // The activity to compare against, taken before the gesture rather than assumed empty.
    const before = await whisparrActivity(instance);

    // The gesture itself: the narrower scope, on an entity the instance already holds.
    await monitorControl(page).click();
    await expect(menu, "the control did not reopen its menu").toBeVisible();
    await menu.getByRole("menuitemradio", { name: SCOPE_FUTURE_SCENES }).click();

    // The extension's own read, asserted before the control's name and never instead of it. The two
    // can disagree, and which one is wrong is what a failure has to say: a server reporting the
    // studio monitored while the control still offers to monitor it is a browser that did not
    // follow. A refusal here is worse than a plain false, because a refusal that leaves the menu
    // available is dropped at the control and the gesture then reads as having done nothing.
    const {
      settled: readReportsMonitored,
      value: reported,
      note: lastRead,
    } = await attemptUntil(
      async (_signal, record) => {
        const view = await coveApi.get(
          extensionRoute(`entity/studio/${String(reachableStudio.id)}/monitoring`),
        );
        record(
          `${String(view.status)} monitored=${JSON.stringify(view.json?.monitored)} refusal=${JSON.stringify(view.json?.refusal)}`,
        );
        return view.json?.monitored === true ? { value: view.json } : null;
      },
      {
        timeoutMs: GESTURE_BUDGET_MS,
        intervalMs: 2_000,
        label: "the entity's own monitoring read",
      },
    );
    expect(
      readReportsMonitored,
      `after the gesture the extension's own read never reported the studio monitored within ${GESTURE_BUDGET_MS}ms; it last answered ${lastRead}`,
    ).toBe(true);
    expect(
      reported.refusal,
      `the read reporting the studio monitored carries the refusal ${JSON.stringify(reported.refusal)}, so the server named a reason alongside a state it reports as applied`,
    ).toBe(MONITOR_REFUSAL_NONE);

    await expect(
      monitoredControl(page),
      `the control did not report the studio as monitored within ${GESTURE_BUDGET_MS}ms, though the extension's own read answered ${lastRead} - so the gesture reached the instance and the control did not follow it`,
    ).toBeVisible({ timeout: GESTURE_BUDGET_MS });

    // The instance's own answer rather than the extension's. This is the assertion the container is
    // here for: everything above it could hold against a response this product composed itself.
    const held = await whisparrEntity(instance, "studio", studioForeignId);
    expect(
      held?.monitored,
      `after the gesture the instance reports the studio as ${JSON.stringify(held?.monitored)}, so the click changed nothing where it matters`,
    ).toBe(true);

    // And the menu the instance's new state produces, which is the unmonitor verb rather than a
    // second monitor.
    await expect(
      page
        .getByRole("menu", { name: MONITORED_IN_WHISPARR })
        .getByRole("menuitem", { name: STOP_MONITORING_IN_WHISPARR }),
      "the menu did not follow the state the instance now reports",
    ).toBeVisible();

    // The never-searched observation, watched over a dwell rather than read once: a command the
    // instance has not issued yet is indistinguishable from one it will never issue.
    await page.waitForTimeout(SETTLE_DWELL_MS);
    const after = await whisparrActivity(instance);

    expect(
      after.commandNames.filter((name) => SEARCH_COMMAND.test(name)),
      `the instance's command roster holds a searching command after the monitor gesture, so the gesture started an acquisition. The whole roster was ${JSON.stringify(after.commandNames)}`,
    ).toEqual([]);
    expect(
      after.queueTotal,
      `the instance's queue total moved from ${String(before.queueTotal)} to ${String(after.queueTotal)} across the monitor gesture`,
    ).toBe(before.queueTotal);
  } finally {
    // Before the harness's own stop: the daemon refuses to remove a network a container still holds
    // an endpoint on, and that failure names neither this spec nor its cause.
    await whisparr.stop();
  }
});

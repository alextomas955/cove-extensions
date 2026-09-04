// The bulk buttons on the studios and performers selection bars, in a real host.
//
// 53-09 pinned the registered spelling by reading the built manifest, which proves the extension
// DECLARES the right string. This spec proves the HOST agrees, and that is the half no tier inside
// this repository can reach: the host matches an action's declared types with a literal
// `entityTypes.includes(...)` over a value its selection bar normalizes for the two media plurals
// only. A studio selection arrives as the RAW PLURAL, and a singular registration makes the button
// simply not appear — with no error anywhere, in the host or in the extension.
//
// So the button's PRESENCE is asserted explicitly rather than inferred from a click that worked. An
// inferred assertion cannot tell a missing button from a click that went somewhere else.
//
// THE EMPTY-SELECTION CASE IS NOT REACHABLE HERE, and this is a fact about the host rather than a
// gap. The host's own selection actions component returns nothing at all while no entity is
// selected, so no extension button exists to press and no test written here could fail. That
// behaviour stays asserted on the handler in the unit tier, where a payload carrying no ids is an
// input a test can supply.
//
// THE CANCEL PATH'S LOAD-BEARING ASSERTION IS THAT NOTHING WAS SENT. The absence of a host toast is
// asserted too, but it discriminates nothing on its own: this extension's actions declare
// `suppressSuccessAlert`, so the host raises no alert on the success path either.
//
// THE RUN'S OWN SUMMARY SENTENCE IS NOT ASSERTED HERE, and the reason is a defect rather than a
// choice: the host recomputes a unit-reporting job's summary from its unit tallies and mirrors that
// onto the sub-task, so this extension's composed line - and the separate linking clause 53-18 added
// to it - never reaches the reader. See the note at the assertion itself.
//
// NO SEARCH IS EXECUTED ANYWHERE IN THIS SPEC.
//
// IF THIS SPEC GOES RED, read the job log for a container-not-running line before debugging the UI.
import { test as base, createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { registerRootFolder, startWhisparr } from "@cove-extensions/e2e/whisparr";
import { attemptUntil } from "@cove-extensions/e2e/poll";
import { randomUUID } from "node:crypto";

// Imported, unlike the labels below. This one is asserted for its PRESENCE in the overlay - 53-18
// put it among the stop action's sentences and nothing else checks that it reaches a reader - so a
// copied literal would keep passing after the shipped sentence moved out from under it.
import { UNMONITORING_DOES_NOT_RETRACT } from "../../src/WhisparrSync.Ui/src/common/ui/copy.ts";
import {
  connectWhisparr,
  expect,
  EXTENSION_ID,
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

// Transcribed by hand from the extension's own registration and copy module, never imported.
const BULK_ACTION_LABEL = "Monitor in Whisparr";
const BULK_CHOOSE_AN_ACTION = "Choose what to do with every entity you selected.";
const BULK_CANCEL = "Cancel";
const SCOPE_FUTURE_SCENES = "Future Scenes";
const STOP_MONITORING_IN_WHISPARR = "Stop monitoring in Whisparr";

// The route the overlay's choice is sent to. Watched on the wire, because "nothing was enqueued" is
// only observable as a request that was never made.
const BULK_ROUTE = extensionRoute("entities/bulk-monitor");

// @see entity-monitor.spec.mjs — the same pattern, so this file names no verb that downloads.
const SEARCH_COMMAND = /search/i;

// The host's own job list, which an unrestricted account can read. The extension's status route
// answers one job by id and so cannot say how many a gesture started, which is the whole of the
// one-job claim below.
const HOST_JOBS = "/api/jobs";
const HOST_JOB_HISTORY = "/api/jobs/history";

// How this extension's own jobs are typed, and the two types a selection could produce. A monitor
// gesture over a selection runs the per-entity linking INSIDE its one job; enqueuing one run per
// entity was the measured alternative, and these two counts are what tell the shapes apart.
const OWN_JOB_PREFIX = `ext:${EXTENSION_ID}:`;
const BULK_JOB_TYPE = `${OWN_JOB_PREFIX}monitoring-bulk`;
const REFLECT_OWNED_JOB_TYPE = `${OWN_JOB_PREFIX}reflect-owned`;

// How many entities this spec puts in front of the host, stated rather than derived from a page.
const SEEDED_STUDIOS = 2;
const SEEDED_PERFORMERS = 2;

// Each budget names the operation it bounds, so a failure says which one blew it.
const PAGE_BUDGET_MS = 60_000;
const PAGE_ATTEMPTS = 3;
const BULK_BUTTON_BUDGET_MS = 60_000;
const ENQUEUE_BUDGET_MS = 60_000;
const JOB_BUDGET_MS = 120_000;

// How long the cancel path is watched for a request it must never make. An absence is only as good
// as the window it was watched over.
const CANCEL_DWELL_MS = 5_000;

const test = base.extend({
  bulkHarness: [
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

  // The `page` fixture resolves its address through `baseUrl`, so without this override the browser
  // would drive the worker-shared instance while every API assertion addressed this one.
  baseUrl: async ({ bulkHarness }, use) => {
    await use(bulkHarness.baseUrl);
  },
});

const bulkButton = (page) => page.getByRole("button", { name: BULK_ACTION_LABEL });
const chooserPanel = (page) => page.getByRole("dialog", { name: BULK_CHOOSE_AN_ACTION });

/** Opens `path`, re-navigating while the host has painted its own error boundary in place of it. */
async function visit(page, baseUrl, path, present, label) {
  for (let attempt = 1; attempt <= PAGE_ATTEMPTS; attempt++) {
    await page.goto(`${baseUrl}${path}`);
    const rendered = await present
      .waitFor({ state: "visible", timeout: PAGE_BUDGET_MS })
      .then(() => true)
      .catch(() => false);
    if (rendered) return;
  }
  throw new Error(
    `${label}: nothing rendered at ${baseUrl}${path} across ${PAGE_ATTEMPTS} navigation(s) of ${PAGE_BUDGET_MS}ms each; the page is now at ${page.url()}`,
  );
}

/**
 * Every card's own selection toggle on an entity list page, in DOM order.
 *
 * Matched on the anchored pair rather than on "Select item" alone. Playwright's `name` option is a
 * case-insensitive SUBSTRING match unless it is a regular expression, and "Select item" is a
 * substring of the "Deselect item" the same control renames itself to once it is selected — so the
 * plain-string locator walks back onto an already-selected card and deselects it.
 */
const cardToggles = (page) => page.getByRole("button", { name: /^(Select|Deselect) item$/ });

/**
 * Selects the first `count` cards on an entity list page, addressing each by position.
 *
 * Positional rather than by label, because the label a card carries depends on the state the
 * previous click left it in. The selection is read back off `aria-pressed`: a selection that did not
 * take is otherwise indistinguishable from a bulk button the host declined to render, and only one
 * of those is this spec's subject.
 */
async function selectFirstCards(page, count, where) {
  const toggles = cardToggles(page);
  await expect(
    toggles.first(),
    `${where}: no selectable card rendered within ${PAGE_BUDGET_MS}ms, so nothing could be selected`,
  ).toBeVisible({ timeout: PAGE_BUDGET_MS });
  await expect(
    toggles,
    `${where}: the page does not hold the ${count} cards this spec seeded`,
  ).toHaveCount(count);

  for (let index = 0; index < count; index++) {
    await toggles.nth(index).click();
  }

  const pressed = await toggles.evaluateAll((buttons) =>
    buttons.map((button) => button.getAttribute("aria-pressed")),
  );
  expect(
    pressed,
    `${where}: the cards report ${JSON.stringify(pressed)} after ${count} click(s), so the selection this spec needs never happened and nothing below would be about the extension`,
  ).toEqual(Array.from({ length: count }, () => "true"));
}

test("both bulk buttons appear in the real host, one gesture monitors two real studios, and cancelling sends nothing", async ({
  page,
  baseUrl,
  bulkHarness,
}) => {
  test.setTimeout(900_000);

  const coveApi = createApiClient(
    () => bulkHarness.baseUrl,
    () => bulkHarness.token,
  );

  // Every alert the host raises, so the cancel path can assert none was raised. Registered before
  // anything is driven: a dialog Playwright auto-dismissed before this ran would go unrecorded.
  const alerts = [];
  page.on("dialog", async (dialog) => {
    alerts.push(dialog.message());
    await dialog.dismiss();
  });

  // Every request to the bulk route, so "nothing was enqueued" is observed rather than inferred.
  const bulkRequests = [];
  page.on("request", (request) => {
    if (new URL(request.url()).pathname === BULK_ROUTE) {
      bulkRequests.push(request.method());
    }
  });

  const whisparr = await startWhisparr({
    network: bulkHarness.container.getNetworkNames()[0],
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

    expect(
      await whisparrAcquisitionSurface(instance),
      "the fixture instance has an indexer or a download client, so no never-searched claim may be taken against it",
    ).toEqual({ indexers: 0, downloadClients: 0 });

    const run = randomUUID().slice(0, 8);
    const studioForeignIds = [`cove-e2e-bulk-studio-a-${run}`, `cove-e2e-bulk-studio-b-${run}`];
    const performerForeignIds = [
      `cove-e2e-bulk-performer-a-${run}`,
      `cove-e2e-bulk-performer-b-${run}`,
    ];

    for (const [index, foreignId] of studioForeignIds.entries()) {
      const seeded = await whisparr.seedEntity("v3", {
        kind: "studio",
        foreignId,
        title: `Cove E2E Bulk Studio ${String(index)} ${run}`,
      });
      expect(
        seeded.monitored,
        `the seeded studio ${foreignId} arrived already monitored, so a monitored reading after the gesture would prove nothing`,
      ).toBe(false);
      await seedCoveStudio(coveApi, {
        name: `Bulk Studio ${String(index)} ${run}`,
        remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: foreignId }],
      });
    }

    for (const [index, foreignId] of performerForeignIds.entries()) {
      await whisparr.seedEntity("v3", {
        kind: "performer",
        foreignId,
        title: `Cove E2E Bulk Performer ${String(index)} ${run}`,
      });
      await seedCovePerformer(coveApi, {
        name: `Bulk Performer ${String(index)} ${run}`,
        remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: foreignId }],
      });
    }

    await connectWhisparr(coveApi, whisparr, "v3");

    // The studios selection bar. This is the assertion the whole spec exists for: the host matched
    // the raw plural the bar passes against the string this extension registered.
    await visit(page, baseUrl, "/studios", cardToggles(page).first(), "the studios page");
    await selectFirstCards(page, SEEDED_STUDIOS, "the studios page");
    await expect(
      bulkButton(page),
      `the studios selection bar carries no "${BULK_ACTION_LABEL}" button within ${BULK_BUTTON_BUDGET_MS}ms. ` +
        "The host matches an action's declared entity types by literal membership against the spelling its bar passes, which is the RAW PLURAL for a studio selection; a singular registration makes this button simply not appear, with no error anywhere.",
    ).toBeVisible({ timeout: BULK_BUTTON_BUDGET_MS });

    // The cancel path, taken FIRST so the assertion that nothing was sent is made before this spec
    // has sent anything at all.
    await bulkButton(page).click();
    await expect(
      chooserPanel(page),
      "the bulk button opened no chooser, so there was nothing to cancel",
    ).toBeVisible();
    await chooserPanel(page).getByRole("button", { name: BULK_CANCEL, exact: true }).click();
    await expect(chooserPanel(page), "cancelling did not close the chooser").toBeHidden();
    await page.waitForTimeout(CANCEL_DWELL_MS);
    expect(
      bulkRequests,
      "cancelling the chooser still reached the bulk route, so leaving without choosing enqueues work nobody asked for",
    ).toEqual([]);
    expect(
      alerts,
      "cancelling the chooser raised a host alert. (Note this discriminates little on its own: these actions declare suppressSuccessAlert, so the success path raises none either.)",
    ).toEqual([]);

    // The gesture itself, on the same selection.
    await bulkButton(page).click();
    await expect(chooserPanel(page), "the bulk button did not reopen its chooser").toBeVisible();
    await expect(
      chooserPanel(page).getByRole("button", { name: STOP_MONITORING_IN_WHISPARR }),
      "the chooser offers no unmonitor verb, so it is not reading the connected generation's capabilities",
    ).toBeVisible();
    // What unmonitoring does NOT do, stated where the choice is made rather than after it. A reader
    // who unmonitors to stop acquisition has not stopped it, and no other sentence in this product
    // says so - so the overlay carrying it is the only place that fact reaches them.
    await expect(
      chooserPanel(page).getByText(UNMONITORING_DOES_NOT_RETRACT, { exact: false }),
      "the chooser offers the unmonitor verb without saying what it leaves behind, so a reader stops Whisparr wanting new scenes and believes they retracted what All Scenes already made wanted",
    ).toBeVisible();

    const enqueued = page.waitForResponse(
      (response) => new URL(response.url()).pathname === BULK_ROUTE,
      { timeout: ENQUEUE_BUDGET_MS },
    );
    await chooserPanel(page).getByRole("button", { name: SCOPE_FUTURE_SCENES }).click();
    const response = await enqueued;
    expect(
      response.status(),
      `the bulk route answered ${response.status()} rather than enqueueing`,
    ).toBeLessThan(400);
    const jobId = (await response.json())?.jobId;
    expect(
      jobId,
      "the bulk route answered without a job id, so nothing below could watch it",
    ).toBeTruthy();
    expect(
      bulkRequests,
      `the one gesture reached the bulk route ${String(bulkRequests.length)} time(s), so a selection enqueued more than one run`,
    ).toEqual(["POST"]);

    // Polled through the extension's OWN status route, which is what a scoped account can watch: the
    // host gates its own job route on unrestricted read.
    const {
      settled,
      value: finished,
      note,
    } = await attemptUntil(
      async (_signal, record) => {
        const status = await coveApi.get(extensionRoute(`job-status/${String(jobId)}`));
        record(`${status.status} with state ${status.json?.status ?? "absent"}`);
        return status.json?.status === "completed" ? { value: status.json } : null;
      },
      { timeoutMs: JOB_BUDGET_MS, intervalMs: 1_000, label: "bulk monitor job" },
    );
    expect(
      settled,
      `the bulk job never reported itself complete within ${JOB_BUDGET_MS}ms; its status route last answered ${note}`,
    ).toBe(true);
    expect(
      finished.entitiesApplied,
      `the job reported ${JSON.stringify(finished)}, so it did not apply the gesture to both selected studios`,
    ).toBe(SEEDED_STUDIOS);

    // The instance's own answer for both, which is the assertion the container is here for.
    for (const foreignId of studioForeignIds) {
      const held = await whisparrEntity(instance, "studio", foreignId);
      expect(
        held?.monitored,
        `after the bulk gesture the instance reports ${foreignId} as ${JSON.stringify(held?.monitored)}`,
      ).toBe(true);
    }

    // 53-18's correction, taken against the instance rather than against the answer. A run reports
    // applied from a READ of each entity, so the count it reports and the count the instance holds
    // are the same number or the read-back is not happening.
    const monitoredOnTheInstance = await Promise.all(
      studioForeignIds.map(async (foreignId) =>
        (await whisparrEntity(instance, "studio", foreignId))?.monitored === true ? 1 : 0,
      ),
    ).then((flags) => flags.reduce((total, flag) => total + flag, 0));
    expect(
      finished.entitiesApplied,
      `the job reported ${String(finished.entitiesApplied)} applied and the instance holds ${String(monitoredOnTheInstance)} of the ${String(SEEDED_STUDIOS)} selected studios monitored, so the reported count is not a read of what the instance does`,
    ).toBe(monitoredOnTheInstance);

    // ONE job for the whole selection, and the per-entity linking inside it. Enqueuing one linking
    // run per entity was the measured alternative, and a selection of a thousand entities is exactly
    // where that difference stops being cosmetic.
    const ownJobs = await Promise.all([coveApi.get(HOST_JOBS), coveApi.get(HOST_JOB_HISTORY)]).then(
      (answers) =>
        answers
          .flatMap((answer) => (Array.isArray(answer.json) ? answer.json : []))
          .filter((job) => String(job.type ?? "").startsWith(OWN_JOB_PREFIX)),
    );
    expect(
      ownJobs.filter((job) => job.type === BULK_JOB_TYPE).length,
      `the one gesture over ${String(SEEDED_STUDIOS)} studios produced ${String(ownJobs.filter((job) => job.type === BULK_JOB_TYPE).length)} bulk job(s). The extension's whole job list was ${JSON.stringify(ownJobs.map((job) => job.type))}`,
    ).toBe(1);
    expect(
      ownJobs.filter((job) => job.type === REFLECT_OWNED_JOB_TYPE),
      `the selection enqueued a separate reflect-owned run per entity rather than doing that work inside its one job. The extension's whole job list was ${JSON.stringify(ownJobs.map((job) => job.type))}`,
    ).toEqual([]);

    // What the run reports, taken on the members it owns rather than on its own sentence.
    //
    // THE COMPOSED LINE DOES NOT REACH A READER, AND THAT IS A DEFECT THIS SPEC RECORDS RATHER THAN
    // PINS. The extension writes its own summary through the final progress report - "N applied, M
    // refused." followed by a separate linking clause, which is the shape 53-18 chose so the
    // per-entity linking is reported apart from the monitor outcomes. The host overwrites it: every
    // unit tally recomputes `Summary` as its own "N of M units succeeded" and then mirrors that onto
    // `SubTask`, so a job that reports units - which this one must, because the browser reads the
    // per-entity counts off them - can never keep a sentence of its own. The reflect-owned and
    // add-all-missing runs are unaffected and their lines are asserted in the sibling spec, because
    // neither reports units. Asserting the absence here would read as coverage of a decision nobody
    // took, so what is asserted is the counts, and the line is left named in the SUMMARY.
    expect(
      {
        total: finished.entitiesTotal,
        applied: finished.entitiesApplied,
        refused: finished.entitiesRefused,
        passedOver: finished.entitiesPassedOver,
      },
      `the bulk run reported ${JSON.stringify(finished)}, which does not account for every selected studio: a reader is told a total that its own parts do not add up to`,
    ).toEqual({
      total: SEEDED_STUDIOS,
      applied: SEEDED_STUDIOS,
      refused: 0,
      passedOver: 0,
    });

    // And nothing acquisitive was started by a gesture that touched two entities at once, watched
    // over the same named window its sibling uses rather than read the moment the poll returned. The
    // poll's own interval is a delay the run happened to have, not a window anyone chose, and an
    // absence bounded by an accident passes on a broken instance as readily as on a correct one.
    await page.waitForTimeout(SETTLE_DWELL_MS);
    const after = await whisparrActivity(instance);
    expect(
      after.commandNames.filter((name) => SEARCH_COMMAND.test(name)),
      `the instance's command roster holds a searching command after the bulk gesture. The whole roster was ${JSON.stringify(after.commandNames)}`,
    ).toEqual([]);

    // The performers selection bar. Its own registration, and the second half of the raw-plural
    // fact: the two are registered separately because the host allows one permission per action.
    await visit(page, baseUrl, "/performers", cardToggles(page).first(), "the performers page");
    await selectFirstCards(page, SEEDED_PERFORMERS, "the performers page");
    await expect(
      bulkButton(page),
      `the performers selection bar carries no "${BULK_ACTION_LABEL}" button within ${BULK_BUTTON_BUDGET_MS}ms; the host matched no action against the spelling its bar passes for a performer selection.`,
    ).toBeVisible({ timeout: BULK_BUTTON_BUDGET_MS });
  } finally {
    await whisparr.stop();
  }
});

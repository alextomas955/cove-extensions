// The Whisparr button on the videos selection bar, in a real containerized host.
//
// TWO TESTS, AND THE SECOND IS ABOUT AN ABSENCE. On v3 the button opens this
// extension's own overlay, one row starts a background run, and a refused row states its refusal
// there and leaves the selection alone. On v2 the registration never reaches the
// manifest, so nothing extension-shaped reaches the selection bar: not the button, and not the
// host's own contributed-action button with nothing in it. Those are different DOM states and only
// one of them is what is promised.
//
// WHY THIS SPEC EXISTS. Nothing below the browser can see the whole path. The host matches an
// action's declared entity types by literal membership against the spelling its own selection bar
// passes, and it normalizes only the two media plurals - so a video selection arrives SINGULAR while
// a studio selection arrives plural. It then resolves the action's handler name against the bundle's
// own map by exact string and dispatches nothing, with no error, when the two differ. Both facts are
// invisible to every tier inside this repository.
//
// THE ROW ORDER IS ASSERTED AS A SEQUENCE, not as a membership. Safest first and the only row that
// can download fourth is the promise; a set comparison passes a menu that has been re-sorted.
//
// THE REFUSAL IS DRIVEN AGAINST A STUBBED ANSWER, and the reason is the harness rather than a
// preference: the search row's bound is 100 scenes and this harness seeds a handful, so a selection
// that exceeds it cannot be made by clicking. What is under test on the browser's side is which
// sentence a code produces, that it is stated in the same overlay, and that the selection survives
// it - so the route is pointed at the answer the route itself gives above the bound. That the route
// gives that answer is settled in the backend suite.
//
// NO SEARCH IS EXECUTED ANYWHERE IN THIS SPEC. The one row that can download is chosen only against
// the stubbed refusal, which sends nothing.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { attemptUntil } from "@cove-extensions/e2e/poll";
import { randomUUID } from "node:crypto";

import {
  expect,
  EXTENSION_ID,
  extensionRoute,
  seedCoveVideo,
  SETTLE_DWELL_MS,
  SPEC_BUDGET_MS,
  STASHDB_ENDPOINT,
  test,
  whisparrActivity,
} from "../../lib/connected-fixture.mjs";
import { visit } from "../../lib/steps.mjs";

// The button's label, transcribed by hand from the registration that declares it. A spec importing
// the same constant would be asserting that a string equals itself.
const BATCH_BUTTON_LABEL = "Whisparr";

// The five rows in the order they are promised in. Transcribed the same way.
const BATCH_ROW_LABELS = ["Add", "Monitor", "Unmonitor", "Search now", "Exclude"];
const MONITOR = BATCH_ROW_LABELS[1];
const SEARCH = BATCH_ROW_LABELS[3];
const BULK_CANCEL = "Cancel";
const BULK_CLOSE = "Close";

// The sentence a selection over the SEARCH row's own bound is refused with, transcribed by hand. The
// limit it names is the point: the other four rows are bounded ten times higher and their sentence
// names that number instead.
const SEARCH_IS_OVER_THE_BOUND =
  "so Cove searches at most 100 scenes in one run, and you selected more";
const SELECTION_IS_OVER_THE_OTHER_BOUND = "Cove acts on at most 1000 entities in one run";

// The route the overlay's choice is sent to. Watched on the wire, because "nothing was enqueued" is
// only observable as a request that was never made.
const BATCH_ROUTE = extensionRoute("scenes/batch");

// What the route answers above the search row's bound, and the code the browser chooses its sentence
// on. Transcribed from the route rather than imported for the reason every other literal here is.
const OVER_THE_SEARCH_BOUND = '{"code":"TOO_MANY_SEARCH_IDS","max":100}';

// The spelling the host's videos selection bar passes, and the one a scene bulk action has to
// declare to be matched. Transcribed by hand from the registration, like every literal here.
const VIDEOS_SELECTION_TYPE = "video";

// The host's own job list, and how this extension's runs are typed in it.
const HOST_JOBS = "/api/jobs";
const HOST_JOB_HISTORY = "/api/jobs/history";
const OWN_JOB_PREFIX = `ext:${EXTENSION_ID}:`;
const SCENE_BATCH_JOB_TYPE = `${OWN_JOB_PREFIX}scene-batch`;

// The job entry's members a reader is shown in the drawer, which are the ones nothing may grow in.
const READER_FACING_STRINGS = ["description", "subTask", "summary", "error"];

// @see entity-monitor-bulk.v3.spec.mjs - the same pattern, so this file names no verb that downloads.
const SEARCH_COMMAND = /search/i;

// How many scenes this spec puts in front of the host, stated rather than derived from a page.
const SEEDED_SCENES = 2;

// Each budget names the operation it bounds, so a failure says which one blew it.
const PAGE_BUDGET_MS = 60_000;
const BATCH_BUTTON_BUDGET_MS = 60_000;
const ENQUEUE_BUDGET_MS = 60_000;
const JOB_BUDGET_MS = 120_000;

// How long the cancel path is watched for a request it must never make. An absence is only as good
// as the window it was watched over.
const CANCEL_DWELL_MS = 5_000;
// A press inside the overlay, bounded like every other wait here. A click left unbounded takes the
// whole test budget when its locator stops matching, which reads as a slow spec rather than a
// missing control.
const ROW_BUDGET_MS = 20_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });
test.use({ generation: "v3" });

const batchButton = (page) => page.getByRole("button", { name: BATCH_BUTTON_LABEL, exact: true });
// The panel heads itself with the product's name and the count of what is selected, and that header
// is its accessible name.
const chooserPanel = (page) =>
  page.getByRole("menu", { name: `Whisparr · ${SEEDED_SCENES} selected` });

/**
 * Every contributed selection-action button the host drew, by the glyph it draws on all of them.
 *
 * The empty-versus-absent distinction in its selection-bar form. The host draws this button for a
 * registered bulk action whatever the extension declares, and draws nothing at all where no action
 * matched, so a non-zero count on v2 is a surface that rendered rather than one
 * that is absent. Scoped inside the page's own main region, which the navigation is not.
 */
const contributedSelectionButtons = (page) =>
  page.locator("main").locator("button:has(svg.lucide-puzzle)");

/** Every card's own selection toggle on a list page, in DOM order. */
const cardToggles = (page) => page.getByRole("button", { name: /^(Select|Deselect) item$/ });

/**
 * Every bulk action this extension registers for a VIDEO selection in the manifest the browser is
 * served.
 *
 * The DOM cannot report this on its own: the host draws no selection bar at all while nothing is
 * selected, so an absent button and an absent registration look alike from the page.
 *
 * Narrowed to the videos bar rather than every bulk action, because the studio and performer
 * monitoring buttons are registered on BOTH generations and refuse in place there. Only the scene
 * selection's own button is absent on v2.
 */
async function registeredVideoBulkActions(api) {
  const manifest = await api.get("/api/extensions/manifest");
  expect(manifest.status, `GET the extension manifest answered ${String(manifest.status)}`).toBe(
    200,
  );
  return (manifest.json?.actions ?? [])
    .filter(
      (entry) =>
        entry.extensionId === EXTENSION_ID &&
        entry.actionType === "bulk" &&
        (entry.entityTypes ?? []).includes(VIDEOS_SELECTION_TYPE),
    )
    .map((entry) => entry.id);
}

/**
 * Selects the first `count` cards, addressing each by position and reading the selection back.
 *
 * Positional rather than by label, because the label a card carries depends on the state the
 * previous click left it in. A selection that did not take is otherwise indistinguishable from a
 * button the host declined to render, and only one of those is this spec's subject.
 */
async function selectFirstCards(page, count, where) {
  const toggles = cardToggles(page);
  await expect(
    toggles.first(),
    `${where}: no selectable card rendered within ${String(PAGE_BUDGET_MS)}ms, so nothing could be selected`,
  ).toBeVisible({ timeout: PAGE_BUDGET_MS });

  for (let index = 0; index < count; index++) {
    await toggles.nth(index).click();
  }

  await expect(
    page.getByRole("button", { name: "Deselect item" }),
    `${where}: the cards do not report ${String(count)} selected after ${String(count)} click(s), so the selection this spec needs never happened and nothing below would be about the extension`,
  ).toHaveCount(count);
}

/** How many cards report themselves selected right now. */
const selectedCount = (page) => page.getByRole("button", { name: "Deselect item" }).count();

/**
 * Seeds one scene on the instance and the Cove video that names it, unmonitored.
 *
 * The instance's entry is written into its own datastore rather than added through its API: an add
 * resolves the identifier against the vendor's metadata service, so a scene's mere existence would
 * depend on someone else's uptime. Unmonitored, so a monitoring verb has somewhere to move it to.
 */
async function seedScene(coveApi, whisparr, index, run) {
  const remoteId = randomUUID();
  await whisparr.seedEntity("v3", {
    kind: "scene",
    foreignId: remoteId,
    title: `Cove E2E Batch Scene ${String(index)} ${run}`,
    monitored: false,
  });
  const title = `Batch Scene ${String(index)} ${run}`;
  const video = await seedCoveVideo(coveApi, {
    title,
    remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId }],
  });
  return { ...video, title, remoteId };
}

/** Every job this extension owns that the host's own drawer holds, running or finished. */
async function ownJobs(coveApi) {
  return await Promise.all([coveApi.get(HOST_JOBS), coveApi.get(HOST_JOB_HISTORY)]).then(
    (answers) =>
      answers
        .flatMap((answer) => (Array.isArray(answer.json) ? answer.json : []))
        .filter((job) => String(job.type ?? "").startsWith(OWN_JOB_PREFIX)),
  );
}

/** Whether the instance is monitoring the scene it holds under `remoteId`, read off the instance. */
async function monitoredOnInstance(api, remoteId) {
  const held = await api.get(`/api/v3/movie?stashId=${encodeURIComponent(remoteId)}`);
  const row = Array.isArray(held.json) ? held.json[0] : held.json;
  return row?.monitored ?? null;
}

test.describe("scene batch", () => {
  test("five rows in one fixed order, one background run, and a refusal that keeps the selection", async ({
    page,
    baseUrl,
    connected,
  }) => {
    // A container pair, an extension install, a browser and a real instance. Well above the shared
    // per-test budget, and deliberately its own number rather than a raised default for every spec.
    const { api: coveApi, whisparr } = connected;

    // Every alert the host raises, so the refusal path can assert none was raised. Registered before
    // anything is driven: a dialog Playwright auto-dismissed before this ran would go unrecorded.
    const alerts = [];
    page.on("dialog", async (dialog) => {
      alerts.push(dialog.message());
      await dialog.dismiss();
    });

    // Every request to the batch route, so "nothing was enqueued" is observed rather than inferred.
    const batchRequests = [];
    page.on("request", (request) => {
      if (new URL(request.url()).pathname === BATCH_ROUTE) {
        batchRequests.push(request.method());
      }
    });

    const instance = whisparr.apiFor("v3");

    const run = randomUUID().slice(0, 8);
    const videos = [];
    for (let index = 0; index < SEEDED_SCENES; index++) {
      videos.push(await seedScene(coveApi, whisparr, index, run));
    }

    await visit(page, baseUrl, "/videos", cardToggles(page).first(), "the videos page");
    await selectFirstCards(page, SEEDED_SCENES, "the videos page");

    // The assertion the whole spec exists for: the host matched the singular spelling its videos
    // bar passes against the string this extension registered.
    await expect(
      batchButton(page),
      `the videos selection bar carries no "${BATCH_BUTTON_LABEL}" button within ${String(BATCH_BUTTON_BUDGET_MS)}ms. ` +
        "The host matches an action's declared entity types by literal membership against the spelling its bar passes, which is the SINGULAR for a video selection; a plural registration makes this button simply not appear, with no error anywhere.",
    ).toBeVisible({ timeout: BATCH_BUTTON_BUDGET_MS });

    // The locator v2's absence is read through, proven here on the generation
    // that draws it. A locator that matched nothing would report an absence on both.
    await expect(
      contributedSelectionButtons(page),
      "the host drew no contributed selection button on the generation that registers one, so the absence asserted on the other generation would prove nothing",
    ).toHaveCount(1);

    // The rows, read as an ordered list of the names they announce.
    await batchButton(page).click();
    await expect(
      chooserPanel(page),
      "the Whisparr button opened no chooser, so nothing below is about the rows",
    ).toBeVisible();
    const offered = await chooserPanel(page)
      .getByRole("menuitem")
      .evaluateAll((rows) => rows.map((row) => row.textContent?.trim()));
    expect(
      offered,
      "the overlay does not offer the five rows in the order it promises: safest first, the only row that can download fourth, and the row that changes what Whisparr accepts in future last",
    ).toEqual([...BATCH_ROW_LABELS, BULK_CANCEL]);

    // One glyph and one name per row, and no paragraph anywhere inside the panel.
    expect(
      await chooserPanel(page).locator("p").count(),
      "the chooser draws a paragraph, so a row states prose the panel is no longer meant to carry",
    ).toBe(0);

    // The cancel path, taken FIRST so the assertion that nothing was sent is made before this spec
    // has sent anything at all.
    await chooserPanel(page)
      .getByRole("menuitem", { name: BULK_CANCEL, exact: true })
      .click({ timeout: ROW_BUDGET_MS });
    await expect(chooserPanel(page), "cancelling did not close the chooser").toBeHidden();
    await page.waitForTimeout(CANCEL_DWELL_MS);
    expect(
      batchRequests,
      "cancelling the chooser still reached the batch route, so leaving without choosing enqueues work nobody asked for",
    ).toEqual([]);
    expect(
      await selectedCount(page),
      "cancelling the chooser cleared the selection, so a reader who changed their mind has to make it again",
    ).toBe(SEEDED_SCENES);

    // The gesture itself, on the same selection.
    //
    // THE MONITOR ROW, and the reason is what the harness can seed. A scene's entry is written
    // into the instance's own datastore, because adding one through its API resolves the
    // identifier against the vendor's metadata service; so every scene this spec puts in front of
    // the host is already held, and Add over a held scene is correctly refused with nothing sent.
    // Monitor is the row whose effect this harness can both cause and read back off the instance.
    await batchButton(page).click();
    await expect(
      chooserPanel(page),
      "the Whisparr button did not reopen its chooser",
    ).toBeVisible();
    const enqueued = page.waitForResponse(
      (response) => new URL(response.url()).pathname === BATCH_ROUTE,
      { timeout: ENQUEUE_BUDGET_MS },
    );
    await chooserPanel(page)
      .getByRole("menuitem", { name: MONITOR, exact: true })
      .click({ timeout: ROW_BUDGET_MS });
    const response = await enqueued;
    expect(
      response.status(),
      `the batch route answered ${String(response.status())} rather than enqueueing`,
    ).toBeLessThan(400);
    expect(
      batchRequests,
      `the one gesture reached the batch route ${String(batchRequests.length)} time(s), so a selection enqueued more than one run`,
    ).toEqual(["POST"]);

    // Cove's own job drawer, which is where this extension says the answer appears.
    //
    // THE COUNTS ARE THE ENTRY'S OWN TALLIES, NOT A SENTENCE. Cove recomputes a unit-reporting
    // job's summary from those tallies and mirrors it onto the sub-task, so this extension's
    // composed line never reaches the drawer and hunting it here would be waiting for a string
    // the host has already overwritten. The tallies are what a reader is shown.
    const {
      settled,
      value: entry,
      note,
    } = await attemptUntil(
      async (_signal, record) => {
        const jobs = await ownJobs(coveApi);
        record(JSON.stringify(jobs));
        const batch = jobs.filter((job) => job.type === SCENE_BATCH_JOB_TYPE);
        const reported = batch.find((job) => job.status === "completed");
        return reported === undefined ? null : { value: { batch, reported } };
      },
      { timeoutMs: JOB_BUDGET_MS, intervalMs: 1_000, label: "scene batch job" },
    );
    expect(
      settled,
      `no scene batch run completed in the host's job drawer within ${String(JOB_BUDGET_MS)}ms; the drawer last held ${note}`,
    ).toBe(true);
    expect(
      entry.batch.length,
      `the one gesture over ${String(SEEDED_SCENES)} scenes produced ${String(entry.batch.length)} run(s) in the drawer`,
    ).toBe(1);

    expect(
      {
        total: entry.reported.unitsTotal,
        succeeded: entry.reported.unitsSucceeded,
        failed: entry.reported.unitsFailed,
        skipped: entry.reported.unitsSkipped,
      },
      `the run reported ${JSON.stringify(entry.reported)}, so the gesture did not reach every selected scene`,
    ).toEqual({ total: SEEDED_SCENES, succeeded: SEEDED_SCENES, failed: 0, skipped: 0 });

    // NOTHING THE READER IS SHOWN GROWS WITH THE SELECTION, asserted on the whole of the
    // extension's own line and on the absence of the scenes' own names.
    //
    // A Cove id is not what is looked for here: an id of a digit or two is a substring of the
    // selected count, of the run's own timestamps and of its id, so a substring hunt for one
    // reports a line that names nothing. A seeded title is unambiguous, and a line that listed
    // what a run did would carry them.
    expect(
      entry.reported.description,
      "the run's description is not the fixed sentence this extension composes, so what a reader is shown is derived from the selection itself",
    ).toBe(`[Whisparr Sync] Scenes, ${String(SEEDED_SCENES)} selected`);
    for (const video of videos) {
      const naming = READER_FACING_STRINGS.filter((key) =>
        String(entry.reported[key] ?? "").includes(video.title),
      );
      expect(
        naming,
        `the drawer entry names the scene "${video.title}" in ${naming.join(", ")}, so what it reports grows with the selection`,
      ).toEqual([]);
    }

    // What the instance holds, which is the assertion the container is here for. A count read off
    // the run's own answer agrees with itself whether or not anything reached Whisparr.
    for (const video of videos) {
      expect(
        await monitoredOnInstance(instance, video.remoteId),
        `after the batch gesture the instance does not report the scene behind Cove video ${String(video.id)} as monitored, so the run's count is not a read of what Whisparr does`,
      ).toBe(true);
    }

    // The refusal, against the answer the route itself gives above the search row's bound. See the
    // header: the bound is 100 scenes and this harness seeds two, so the selection that reaches it
    // cannot be made by clicking.
    await page.route(`**${BATCH_ROUTE}`, async (route) => {
      await route.fulfill({
        status: 400,
        contentType: "application/json",
        body: OVER_THE_SEARCH_BOUND,
      });
    });

    await batchButton(page).click();
    await expect(
      chooserPanel(page),
      "the Whisparr button did not reopen its chooser",
    ).toBeVisible();
    await chooserPanel(page)
      .getByRole("menuitem", { name: SEARCH, exact: true })
      .click({ timeout: ROW_BUDGET_MS });

    // The same overlay, reopened with the sentence and no row to choose.
    await expect(
      page.getByText(SEARCH_IS_OVER_THE_BOUND, { exact: false }),
      "the refused row did not state the limit that applied in the overlay it was chosen in, so the outcome reached the reader somewhere else or nowhere",
    ).toBeVisible({ timeout: ENQUEUE_BUDGET_MS });
    await expect(
      page.getByText(SELECTION_IS_OVER_THE_OTHER_BOUND, { exact: false }),
      "the refusal names the other four rows' bound, so a reader refused at 100 is told about 1000",
    ).toHaveCount(0);
    expect(
      alerts,
      `the refusal reached the host's own alert, which shows the answer's raw text: ${JSON.stringify(alerts)}`,
    ).toEqual([]);

    await page
      .getByRole("menuitem", { name: BULK_CLOSE, exact: true })
      .click({ timeout: ROW_BUDGET_MS });
    expect(
      await selectedCount(page),
      "the refusal cleared the selection, so a reader told to select fewer has nothing left to select fewer of",
    ).toBe(SEEDED_SCENES);

    // And nothing acquisitive was started by any of it, watched over the same named window its
    // siblings use rather than read the moment the last assertion returned.
    await page.unroute(`**${BATCH_ROUTE}`);
    await page.waitForTimeout(SETTLE_DWELL_MS);
    const after = await whisparrActivity(instance);
    expect(
      after.commandNames.filter((name) => SEARCH_COMMAND.test(name)),
      `the instance's command roster holds a searching command after this spec ran. The whole roster was ${JSON.stringify(after.commandNames)}`,
    ).toEqual([]);
  });

  // Its own block, so this execution starts the older generation's container and not the
  // newer one's.
  test.describe("the older generation", () => {
    test.use({ generation: "v2" });

    test("v2 draws no Whisparr button on the videos selection bar, and no wrapper for one either", async ({
      page,
      baseUrl,
      connected,
    }) => {
      const { api: coveApi } = connected;

      // No entry on the instance and none needed. Nothing is asked of it on this generation, and a
      // seeded entry would make an absent button look like a button with nothing to say.
      await seedCoveVideo(coveApi, {
        title: `Older ${randomUUID().slice(0, 8)}`,
        remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: randomUUID() }],
      });

      // The registration is what removes the surface, so the set the page was built from is read
      // before the page is. The host draws no selection bar at all while nothing is selected, so an
      // absent button and an absent registration are indistinguishable from the page alone.
      expect(
        await registeredVideoBulkActions(coveApi),
        "v2 registers a videos selection action, so a surface it has no meaning on reached the manifest the host served",
      ).toEqual([]);

      await visit(page, baseUrl, "/videos", cardToggles(page).first(), "the videos page");
      await selectFirstCards(page, 1, "the videos page on v2");
      await page.waitForTimeout(SETTLE_DWELL_MS);

      await expect(
        batchButton(page),
        `v2 drew a ${BATCH_BUTTON_LABEL} button on the videos selection bar, so the registration is not conditional on the stored generation`,
      ).toHaveCount(0);

      await expect(
        contributedSelectionButtons(page),
        "the host drew its own button for a contributed selection action, so this surface renders empty rather than being absent",
      ).toHaveCount(0);
    });
  });
});

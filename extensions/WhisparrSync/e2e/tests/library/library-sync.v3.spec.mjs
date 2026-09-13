// Counting a library, offering it to the instance, and offering it again, against a real instance.
//
// The subject is what the INSTANCE holds afterwards, not what the section wrote on screen. A
// rendered sentence says the extension believes something happened; the instance's own rows say
// whether it did. The three counts are read off the page because those figures are the reader's
// whole basis for pressing the button, and they are asserted against tallies this spec seeded.
//
// THE RE-RUN IS THE POINT. Offering a scene the instance already holds must leave one row and start
// no download, however many times it is offered. Both halves are asserted on the instance: one row
// per identifier after three offers of it, and a download queue still empty.
//
// A REGISTRATION LANDING IS NOT REACHABLE HERE, and that is a fact about the sealed container rather
// than a gap in the product. This generation resolves an add's identifier against its own metadata
// service before it will create a catalogue row, and no stand-in for that service is wired for it,
// so an identifier invented by a test is refused however the extension composed the request. The
// refusals are therefore asserted by count: a fixture that later gains such a stand-in makes this
// test red rather than quietly changing what it means. A registration landing, and the not-yet-there
// figure falling behind it, are observed against live instances in this phase's live verification.
//
// WHAT THE MONITOR CHOICE DECIDES IS THE SCENE THE INSTANCE ALREADY HELD. A scene this product
// registers arrives monitored, because the add resource it composes says so and suppresses the
// search beside it. So the choice cannot be read off a newly registered row at all: the row it
// decides is the one the instance held BEFORE the run, which is the difference between monitoring
// what was just added and monitoring what the reader owns.
//
// NO SEARCH IS EXECUTED ANYWHERE IN THIS SPEC. The instance is asserted to hold nothing in its
// download queue and to have run no command whose name says it searches.
//
// THE INSTANCE IS DISPOSABLE. It is a container this test's own fixture starts and that fixture's
// cleanup stack stops, so there is no saved state to put back; the Cove installation beside it is
// equally per-test. Nothing here writes to an instance anybody uses.
//
// IF THIS SPEC GOES RED, read the run's own tallies in the failure message before debugging the UI:
// they say what the run decided, and an instance-side poll says only what did not arrive.
import { pollUntil } from "@cove-extensions/e2e/poll";

import {
  expect,
  EXTENSION_ID,
  extensionRoute,
  searching,
  seedCoveVideo,
  SPEC_BUDGET_MS,
  STASHDB_ENDPOINT,
  test,
  whisparrAcquisitionSurface,
  whisparrActivity,
} from "../../lib/connected-fixture.mjs";
import { SETTINGS_PAGE_PATH } from "../../lib/contract.mjs";

// Transcribed by hand from the extension's own copy module, never imported: a spec importing the
// string it asserts on agrees with whatever that module says.
const COUNT = "Count what would sync";
const RECOUNT = "Refresh";
const SYNC_LIBRARY = "Sync library to Whisparr";
const ALSO_MONITOR = "Also monitor what it syncs";
const NOT_YET_THERE = "Not yet in Whisparr";
const ALREADY_THERE = "Already in Whisparr";
const SKIPPED = "Skipped, cannot be identified";

/** How this extension's own library runs are typed on the host's job list. */
const SYNC_JOB_TYPE = `ext:${EXTENSION_ID}:sync-library`;

// The host's own job surfaces. A finished job moves off the first onto the second, so both are read.
const HOST_JOBS = "/api/jobs";
const HOST_JOB_HISTORY = "/api/jobs/history";

// What this spec puts in the library, stated rather than derived from a read. One scene the instance
// already holds, two whose identifiers it cannot resolve, and one carrying no identifier at all.
const ALREADY_HELD_SCENES = 1;
const UNRESOLVABLE_SCENES = 2;
const UNIDENTIFIED_SCENES = 1;
const IDENTIFIED_SCENES = ALREADY_HELD_SCENES + UNRESOLVABLE_SCENES;

// Each budget names the operation it bounds, so a failure says which one blew it.
const PAGE_BUDGET_MS = 60_000;
const COUNT_BUDGET_MS = 180_000;
const RUN_BUDGET_MS = 240_000;

// How many navigations the settings visit is allowed. The host resolves an unknown settings key only
// once extensions have loaded, and rewrites the address away from this panel when it resolves none;
// nothing but a fresh navigation recovers that.
const VISIT_ATTEMPTS = 3;

test.describe.configure({ timeout: SPEC_BUDGET_MS });
test.use({ generation: "v3" });

/** The figure drawn beside one count's label, as a reader sees it. */
const countValue = (page, label) =>
  page.getByText(label, { exact: true }).locator("xpath=following-sibling::span[1]");

/**
 * Opens the extension's settings panel, retrying the navigation itself.
 *
 * @see ../settings/settings-panel.spec.mjs for why the retry here is a navigation and not a wait.
 */
async function visitSettings(page, baseUrl) {
  const panelUrl = `${baseUrl}${SETTINGS_PAGE_PATH}`;
  const counter = page.getByRole("button", { name: new RegExp(`^(${COUNT}|${RECOUNT})$`) });

  for (let attempt = 1; attempt <= VISIT_ATTEMPTS; attempt++) {
    await page.goto(panelUrl);
    const mounted = await counter
      .waitFor({ state: "visible", timeout: PAGE_BUDGET_MS })
      .then(() => true)
      .catch(() => false);
    if (mounted) return;
  }

  await expect(
    counter,
    `the sync section never rendered at ${panelUrl} across ${VISIT_ATTEMPTS} navigation(s); the page is now at ${page.url()}`,
  ).toBeVisible();
}

/** Presses the count control and reads back the three figures the section then draws. */
async function countAndRead(page, name) {
  await page.getByRole("button", { name, exact: true }).click();
  await expect(
    countValue(page, NOT_YET_THERE),
    "the count never answered with a not-yet-there figure",
  ).toBeVisible({ timeout: COUNT_BUDGET_MS });

  return {
    notYetThere: await countValue(page, NOT_YET_THERE).innerText(),
    alreadyThere: await countValue(page, ALREADY_THERE).innerText(),
    skipped: await countValue(page, SKIPPED).innerText(),
  };
}

/**
 * Confirms the dialog the sync control raises, waits for the run, and answers its own tallies.
 *
 * The run is found on the host's job list rather than from the enqueue's answer, because the gesture
 * under test is a press and a press hands the caller nothing. `alreadyFinished` is what tells this
 * run from the one before it, since every run of this spec carries the same job type.
 */
async function syncAndWait(page, api, alreadyFinished) {
  await page.getByRole("button", { name: SYNC_LIBRARY, exact: true }).first().click();
  const confirm = page.getByRole("dialog", { name: SYNC_LIBRARY });
  await expect(confirm, "the sync control asked for no confirmation").toBeVisible({
    timeout: PAGE_BUDGET_MS,
  });
  await confirm.getByRole("button", { name: SYNC_LIBRARY, exact: true }).click();

  const ended = (
    await pollUntil(
      async () => {
        const running = (await api.get(HOST_JOBS)).json ?? [];
        const history = (await api.get(HOST_JOB_HISTORY)).json ?? [];
        return [...running, ...history].filter(
          (job) =>
            job?.type === SYNC_JOB_TYPE &&
            !alreadyFinished.has(job.id) &&
            /completed|failed|cancelled/i.test(String(job.status)),
        );
      },
      (jobs) => jobs.length > 0,
      { timeoutMs: RUN_BUDGET_MS, intervalMs: 2000, label: "a library run to finish" },
    )
  )[0];
  alreadyFinished.add(ended.id);

  expect(
    String(ended.status).toLowerCase(),
    `the run did not complete: ${JSON.stringify(ended)}`,
  ).toBe("completed");

  const reported = (await api.get(extensionRoute(`job-status/${String(ended.id)}`))).json;
  expect(reported?.error ?? null, `the run faulted: ${String(reported?.error)}`).toBeNull();
  return reported;
}

/** Every catalogue row the instance holds for one identifier. */
const rowsFor = async (instance, foreignId) =>
  ((await instance.get("/api/v3/movie")).json ?? []).filter((row) => row.foreignId === foreignId);

test("a library sync counts, offers, re-offers and monitors what the reader owns", async ({
  page,
  api,
  baseUrl,
  connected,
}) => {
  const { instance, run, studio, whisparr } = connected;

  // Seeded unmonitored: this is the row the monitor choice decides, and one seeded monitored would
  // agree with either choice.
  const heldScene = `cove-e2e-held-scene-${run}`;
  await whisparr.seedEntity("v3", {
    kind: "scene",
    foreignId: heldScene,
    title: `Cove E2E Held Scene ${run}`,
    monitored: false,
  });

  const identified = [heldScene, `cove-e2e-unheld-a-${run}`, `cove-e2e-unheld-b-${run}`];
  expect(identified, "the seeded tallies and the seeded rows disagree").toHaveLength(
    IDENTIFIED_SCENES,
  );
  for (const remoteId of identified) {
    await seedCoveVideo(api, {
      title: remoteId,
      studioId: studio.id,
      remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId }],
    });
  }

  // The skipped column's own row. Without it that figure would be a zero, which a count that never
  // looked would answer just as well.
  await seedCoveVideo(api, { title: `Unidentified ${run}`, studioId: studio.id });

  // Read rather than assumed: a fixture that grew an indexer would make every gesture here
  // acquisitive, and no assertion below would notice.
  expect(
    await whisparrAcquisitionSurface(instance),
    "the instance can acquire, so nothing below bounds what a registration could start",
  ).toEqual({ indexers: 0, downloadClients: 0 });
  const before = await whisparrActivity(instance);

  await visitSettings(page, baseUrl);

  const counted = await countAndRead(page, COUNT);
  expect(counted, "the count disagreed with the library this test seeded").toEqual({
    notYetThere: String(UNRESOLVABLE_SCENES),
    alreadyThere: String(ALREADY_HELD_SCENES),
    skipped: String(UNIDENTIFIED_SCENES),
  });

  const finished = new Set();
  const firstRun = await syncAndWait(page, api, finished);

  expect(firstRun?.entitiesTotal, "the run did not reach every identified scene").toBe(
    IDENTIFIED_SCENES,
  );
  expect(
    firstRun?.entitiesPassedOver,
    "the run did not recognise the scene the instance already holds",
  ).toBe(ALREADY_HELD_SCENES);
  expect(
    { applied: firstRun?.entitiesApplied, refused: firstRun?.entitiesRefused },
    "the sealed instance resolved an identifier a test invented, so the refusal counts this spec is written around no longer hold",
  ).toEqual({ applied: 0, refused: UNRESOLVABLE_SCENES });

  // The whole of the re-offer claim, on the identifier the instance held before the run started.
  expect(
    await rowsFor(instance, heldScene),
    "offering a scene the instance already held created a second row for it",
  ).toHaveLength(1);

  // With the choice off the run changes no flag on a row the instance already had. A newly
  // registered row arrives monitored from the add resource itself, so it says nothing either way.
  expect(
    (await rowsFor(instance, heldScene))[0]?.monitored,
    "the run monitored a scene the instance already held, with the monitor choice off",
  ).toBe(false);

  // A run that registered nothing leaves the three figures where they were, which is the honest
  // second reading here and is asserted rather than skipped: a count that answered anything else
  // after a run that created nothing would be reporting a library it did not read.
  expect(
    await countAndRead(page, RECOUNT),
    "the second count disagrees with a run that registered nothing",
  ).toEqual(counted);

  // The same library again, with the choice on. What changes is the row the reader already owned.
  await page.getByRole("switch", { name: ALSO_MONITOR, exact: true }).click();
  const secondRun = await syncAndWait(page, api, finished);
  expect(secondRun?.entitiesTotal, "the second run reached a different library").toBe(
    IDENTIFIED_SCENES,
  );

  const monitored = await pollUntil(
    () => rowsFor(instance, heldScene),
    (rows) => rows[0]?.monitored === true,
    {
      timeoutMs: RUN_BUDGET_MS,
      intervalMs: 2000,
      label: "the scene the instance already held is monitored",
    },
  );
  expect(
    monitored,
    "a third offer of the scene the instance already held created a second row for it",
  ).toHaveLength(1);

  // Neither run may have started an acquisition. The queue is the observable form of one, and the
  // roster is what says no command whose name searches was run.
  const after = await whisparrActivity(instance);
  expect(after.queueTotal, "a run put something in the instance's download queue").toBe(
    before.queueTotal,
  );
  expect(after.queueTotal, "the instance is downloading something").toBe(0);
  expect(searching(after.commandNames), "a run ran a command whose name says it searches").toEqual(
    searching(before.commandNames),
  );
});

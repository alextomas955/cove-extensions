// A library sync on the older generation, driven against the database engine Cove itself runs on.
//
// THE ENGINE IS THE SUBJECT. The site pass reads the scenes under a site while the run's own site
// stream is still open, and Postgres serves one reader per connection. The .NET suite for this pass
// is SQLite-backed and permits the nested read, so a containerized run is the only check here that
// can see it.
//
// A SCENE ROW LANDING MONITORED IS NOT REACHABLE HERE. This generation addresses a scene by a number
// its metadata service issues, and that client builds its request against an address no container
// stand-in answers. So the owned scene is counted as one the run could not monitor, and that count
// is the evidence the pass reached it.
import { pollUntil } from "@cove-extensions/e2e/poll";

import {
  expect,
  EXTENSION_ID,
  extensionRoute,
  seedCoveVideo,
  SPEC_BUDGET_MS,
  test,
  THEPORNDB_ENDPOINT,
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
const SKIPPED = "Skipped, no metadata id";

/** How this extension's own library runs are typed on the host's job list. */
const SYNC_JOB_TYPE = `ext:${EXTENSION_ID}:sync-library`;

// The host's own job surfaces. A finished job moves off the first onto the second, so both are read.
const HOST_JOBS = "/api/jobs";
const HOST_JOB_HISTORY = "/api/jobs/history";

// What the run's ending says about a pass that reached exactly the one scene the reader owns and
// could put a number on none of them. A pass that reached no scene would end on the first clause
// with nothing after it.
const REACHED_ONE_SCENE = "0 scenes monitored, 1 scene not monitored";

// Each budget names the operation it bounds, so a failure says which one blew it.
const PAGE_BUDGET_MS = 60_000;
const COUNT_BUDGET_MS = 180_000;
const RUN_BUDGET_MS = 240_000;

// How many navigations the settings visit is allowed. The host resolves an unknown settings key only
// once extensions have loaded, and rewrites the address away from this panel when it resolves none;
// nothing but a fresh navigation recovers that.
const VISIT_ATTEMPTS = 3;

test.describe.configure({ timeout: SPEC_BUDGET_MS });
test.use({ generation: "v2" });

/** The figure drawn beside one count's label, as a reader sees it. */
const countValue = (page, label) =>
  page.getByText(label, { exact: true }).locator("xpath=following-sibling::span[1]");

/**
 * The sync control in either state.
 *
 * A disabled control carries its reason off-screen inside its own element, which is part of its
 * accessible name, so the match is anchored at the front rather than exact.
 */
const syncControl = (page) =>
  page.getByRole("button", { name: new RegExp(`^${SYNC_LIBRARY}`) }).first();

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
 * under test is a press and a press hands the caller nothing.
 */
async function syncAndWait(page, api) {
  await syncControl(page).click();
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
            job?.type === SYNC_JOB_TYPE && /completed|failed|cancelled/i.test(String(job.status)),
        );
      },
      (jobs) => jobs.length > 0,
      { timeoutMs: RUN_BUDGET_MS, intervalMs: 2000, label: "a library run to finish" },
    )
  )[0];

  const reported = (await api.get(extensionRoute(`job-status/${String(ended.id)}`))).json;
  expect(reported?.error ?? null, `the run faulted: ${String(reported?.error)}`).toBeNull();
  expect(
    String(ended.status).toLowerCase(),
    `the run did not complete: ${JSON.stringify(ended)}`,
  ).toBe("completed");

  return reported;
}

test("a library sync monitors the scenes the reader owns under a site on Cove's own database engine", async ({
  page,
  api,
  baseUrl,
  connected,
}) => {
  const { run, studio } = connected;

  // The one scene the reader owns under the registered site, and so the one scene the monitor pass
  // has to reach. Without it the pass enumerates nothing and the run proves nothing.
  await seedCoveVideo(api, {
    title: `Cove E2E Owned Scene ${run}`,
    studioId: studio.id,
    remoteIds: [{ endpoint: THEPORNDB_ENDPOINT, remoteId: `cove-e2e-owned-scene-${run}` }],
  });

  await visitSettings(page, baseUrl);

  const counted = await countAndRead(page, COUNT);
  expect(counted, "the count disagreed with the library this test seeded").toEqual({
    notYetThere: "0",
    alreadyThere: "1",
    skipped: "0",
  });

  // The decoupling, observed: with the choice off there is nothing left for a run to register, and
  // the run below therefore has work to do only because monitoring was asked for.
  await expect(
    syncControl(page),
    "the sync control offered a run over a library the instance already holds in full",
  ).toBeDisabled();

  await page.getByRole("switch", { name: ALSO_MONITOR, exact: true }).click();
  await expect(
    syncControl(page),
    "the monitor choice left the sync control unavailable",
  ).toBeEnabled();

  const reported = await syncAndWait(page, api);

  expect(
    String(reported?.summary),
    "the run did not reach the one scene the reader owns under the site",
  ).toContain(REACHED_ONE_SCENE);

  expect(
    await connected.studioMonitored(),
    "the run monitored the site row itself rather than the scenes under it",
  ).toBe(false);

  // A run that registered nothing leaves the three figures where they were.
  expect(
    await countAndRead(page, RECOUNT),
    "the second count disagrees with a run that registered nothing",
  ).toEqual(counted);
});

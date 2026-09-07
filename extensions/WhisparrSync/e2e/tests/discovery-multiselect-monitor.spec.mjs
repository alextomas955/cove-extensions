// Drives the Missing tab's MULTI-SELECT + bulk Monitor through the running host, proving the workflow: select
// several cards → the selection-actions bar shows the count → the bar's Monitor POSTs ONE /discovery/action-all
// with {Op:"monitor", SourceIds:[…]} (the selection) and raises no native success popup (the queued background
// job is the feedback), "Monitor all" POSTs /discovery/action-all with SourceIds OMITTED (the whole entity), and
// an induced non-2xx on the bulk route surfaces (the optimistic flip reverts) rather than being swallowed as a
// silent success. The hermetic harness stands up no Whisparr, so this spec route-intercepts the discovery
// endpoints with a small SYNTHETIC list (numeric "Scene NNNN" titles, synthetic urls — content-safe) and an
// in-test recorder for the bulk route. A live cove-dev drive against real Whisparr v3 (the Job Drawer runs to a
// summary, the marked movies read monitored:true, NO MoviesSearch fired, idempotent re-run) is recorded in the
// plan SUMMARY. No real scene metadata ever enters this fixture.
import {
  test,
  expect,
  seedCorpus,
  routeUsableConfiguration,
} from "../lib/whisparrsync-fixtures.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

const TOTAL = 6;

function syntheticScenes(n) {
  const base = Date.UTC(2018, 0, 1);
  const scenes = [];
  for (let i = 1; i <= n; i++) {
    const pad = String(i).padStart(4, "0");
    scenes.push({
      sourceId: `synthetic-${pad}`,
      title: `Scene ${pad}`,
      releaseDate: new Date(base + i * 86_400_000).toISOString().slice(0, 10),
      entityName: "Synthetic Studio",
      studioName: "Synthetic Studio",
      posterUrl: null,
      coverUrl: `https://cdn.example.test/cover/${pad}.jpg`,
      status: "notAdded",
    });
  }
  return scenes;
}

async function routeDiscovery(
  page,
  { scenes, version, actionAllPosts, actionAllFails },
) {
  // The per-scene verbs below are configuration-guarded; without this the spec inherits the ambient
  // instance's quality profile and dims the very controls it came to click.
  await routeUsableConfiguration(page);

  await page.route("**/discovery/entity", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        scenes,
        entityName: "Synthetic Studio",
        state: "ok",
        source: "whisparr",
        version,
      }),
    });
  });
  await page.route("**/discovery/count**", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ count: scenes.length }),
    });
  });
  await page.route("**/discovery/action-all", async (route) => {
    actionAllPosts.push(route.request().postDataJSON());
    if (actionAllFails.value) {
      await route.fulfill({
        status: 502,
        contentType: "application/json",
        body: JSON.stringify({ result: "unreachable" }),
      });
      return;
    }
    // A background-job route answers with a real {jobId, description} body — the Job Drawer surfaces it.
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        jobId: "job-abc",
        description: "Whisparr: mark missing scenes wanted",
      }),
    });
  });
}

async function openMissingTab(page, baseUrl, studioId) {
  const detail = new EntityDetailPage(page, baseUrl);
  await detail.gotoStudio(studioId);
  const missingTab = page.getByRole("tab", { name: /Missing/ });
  await expect(missingTab).toBeVisible();
  await missingTab.click();
  await expect(missingTab).toHaveAttribute("aria-selected", "true");
}

test("discovery multiselect monitor — select N → bar Monitor posts the selection, Monitor all omits it, failure surfaces", async ({
  harness,
  baseUrl,
  page,
}, testInfo) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()]
    .map((s) => s.studioId)
    .find((id) => id != null);
  expect(
    studioId,
    "seedCorpus should link at least one scene to a studio",
  ).not.toBeUndefined();

  const scenes = syntheticScenes(TOTAL);
  const actionAllPosts = [];
  const actionAllFails = { value: false };
  await routeDiscovery(page, {
    scenes,
    version: "v3",
    actionAllPosts,
    actionAllFails,
  });

  await openMissingTab(page, baseUrl, studioId);

  // The grid renders; the selection bar is absent until something is selected.
  await expect
    .poll(() =>
      page
        .getByRole("list", { name: "Missing scenes" })
        .getByRole("button", { name: /^Select / })
        .count(),
    )
    .toBe(TOTAL);
  const barMonitor = page.getByRole("button", { name: "Monitor", exact: true });
  await expect(barMonitor).toHaveCount(0);

  // ---- (1) multi-select three cards → the bar shows the count + the Monitor action ----
  await page.getByRole("button", { name: "Select Scene 0001" }).click();
  await page.getByRole("button", { name: "Select Scene 0002" }).click();
  await page.getByRole("button", { name: "Select Scene 0003" }).click();
  await expect(page.getByText(/3 selected/i)).toBeVisible();
  await expect(barMonitor).toBeVisible();

  // ---- (2) the bar's Monitor POSTs ONE /discovery/action-all with {Op:"monitor", SourceIds:[the selection]} ----
  await barMonitor.click();
  await expect.poll(() => actionAllPosts.length).toBe(1);
  expect(actionAllPosts[0]).toMatchObject({
    Op: "monitor",
    Kind: "studio",
    CoveEntityId: studioId,
  });
  expect(actionAllPosts[0].SourceIds.slice().sort()).toEqual([
    "synthetic-0001",
    "synthetic-0002",
    "synthetic-0003",
  ]);
  // The selection clears once the job is enqueued; the bar goes away. No native success popup — the queued job
  // is the feedback (this test would fail if a window.alert/dialog blocked the flow).
  await expect(page.getByText(/selected/i)).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("after-bar-monitor.png") });

  // ---- (3) "Monitor all" POSTs /discovery/action-all with SourceIds OMITTED (the whole entity) ----
  await page
    .getByRole("button", { name: "Mark every missing scene wanted" })
    .click();
  await expect.poll(() => actionAllPosts.length).toBe(2);
  expect(actionAllPosts[1]).toMatchObject({
    Op: "monitor",
    Kind: "studio",
    CoveEntityId: studioId,
  });
  expect("SourceIds" in actionAllPosts[1]).toBe(false);
});

test("discovery multiselect monitor — an induced bulk failure reverts the optimistic flip (not a silent success)", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()]
    .map((s) => s.studioId)
    .find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  const scenes = syntheticScenes(TOTAL);
  const actionAllPosts = [];
  const actionAllFails = { value: true };
  await routeDiscovery(page, {
    scenes,
    version: "v3",
    actionAllPosts,
    actionAllFails,
  });

  await openMissingTab(page, baseUrl, studioId);
  await expect
    .poll(() =>
      page
        .getByRole("list", { name: "Missing scenes" })
        .getByRole("button", { name: /^Select / })
        .count(),
    )
    .toBe(TOTAL);

  // Select two, bulk-Monitor → the route 502s. The optimistic flip must REVERT (the cards are not left stuck
  // "Wanted"), so a failed bulk is never a silent success. Their per-card Monitor affordance returns.
  await page.getByRole("button", { name: "Select Scene 0001" }).click();
  await page.getByRole("button", { name: "Select Scene 0002" }).click();
  await page.getByRole("button", { name: "Monitor", exact: true }).click();
  await expect.poll(() => actionAllPosts.length).toBe(1);

  // The two targeted cards revert to their pre-flip state — their "Mark this scene wanted" button reappears
  // (a stuck confirmed-wanted state would mean the failure was swallowed as success).
  await expect
    .poll(() =>
      page.getByRole("button", { name: "Mark this scene wanted" }).count(),
    )
    .toBe(TOTAL);
});

test("discovery multiselect — Invert flips the visible selection and Deselect all clears it", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()]
    .map((s) => s.studioId)
    .find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  const scenes = syntheticScenes(TOTAL);
  const actionAllPosts = [];
  const actionAllFails = { value: false };
  await routeDiscovery(page, {
    scenes,
    version: "v3",
    actionAllPosts,
    actionAllFails,
  });

  await openMissingTab(page, baseUrl, studioId);
  await expect
    .poll(() =>
      page
        .getByRole("list", { name: "Missing scenes" })
        .getByRole("button", { name: /^Select / })
        .count(),
    )
    .toBe(TOTAL);

  // Select one card → the bar surfaces the count and the Select-all / Invert / Deselect-all cluster.
  await page.getByRole("button", { name: "Select Scene 0001" }).click();
  await expect(page.getByText(/1 selected/i)).toBeVisible();
  await expect(page.getByRole("button", { name: "Invert" })).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Deselect all" }),
  ).toBeVisible();

  // Invert → the selection becomes the visible rows MINUS the one selected (5 of 6): Scene 0001 flips back to an
  // unselected "Select" toggle while the others read "Deselect".
  await page.getByRole("button", { name: "Invert" }).click();
  await expect(
    page.getByText(new RegExp(`${TOTAL - 1} selected`, "i")),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Select Scene 0001" }),
  ).toBeVisible();
  await expect(
    page.getByRole("button", { name: "Deselect Scene 0002" }),
  ).toBeVisible();

  // Deselect all clears the selection and the bar unmounts.
  await page.getByRole("button", { name: "Deselect all" }).click();
  await expect(page.getByText(/selected/i)).toHaveCount(0);
});

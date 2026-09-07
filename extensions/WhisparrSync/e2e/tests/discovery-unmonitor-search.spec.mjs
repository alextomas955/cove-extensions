// Drives the Missing tab's per-card + selection-bar UNMONITOR and SEARCH actions through the running host,
// proving the workflows: a wanted card's Unmonitor POSTs ONE /discovery/action {Op:"unmonitor"}, optimistically
// clears the wanted flag + flips the pill to Unmonitored, and REVERTS on an induced non-2xx (never a silent
// success); a card's Search POSTs ONE {Op:"search"} with a transient spinner and no native alert on failure; the
// selection bar's Unmonitor/Search act over the selected sourceIds via /discovery/action-all; and on v2 the
// per-scene/bar Unmonitor is disabled (the shipped capability copy) while Search stays enabled. The hermetic
// harness stands up no Whisparr, so this spec route-intercepts the discovery endpoints with a SYNTHETIC list
// (numeric "Scene NNNN" titles, some pre-flagged wanted — content-safe) and records the action posts. A live
// cove-dev drive against real Whisparr (Unmonitor flips monitored:true→false with NO grab; Search fires exactly
// one MoviesSearch; open/refresh/monitor/unmonitor/bulk fire none) is recorded in the change summary.
import {
  test,
  expect,
  seedCorpus,
  routeUsableConfiguration,
} from "../lib/whisparrsync-fixtures.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

const TOTAL = 6;
const WANTED = 3; // the first WANTED scenes arrive already on the wanted list (status:"wanted")

function syntheticScenes(n, wanted) {
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
      status: i <= wanted ? "wanted" : "notAdded",
    });
  }
  return scenes;
}

async function routeDiscovery(page, { scenes, version, actionPosts, actionAllPosts, fails }) {
  // The per-scene verbs below are configuration-guarded; without this the spec inherits the ambient
  // instance's quality profile and dims the very controls it came to click.
  await routeUsableConfiguration(page);

  await page.route("**/discovery/entity", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ scenes, entityName: "Synthetic Studio", state: "ok", source: "whisparr", version }),
    });
  });
  await page.route("**/discovery/count**", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ count: scenes.length }),
    });
  });
  await page.route("**/discovery/action", async (route) => {
    const body = route.request().postDataJSON();
    actionPosts.push(body);
    if (fails.value) {
      await route.fulfill({ status: 502, contentType: "application/json", body: JSON.stringify({ result: "unreachable" }) });
      return;
    }
    const ok = body.Op === "search" ? { searched: true } : { unmonitored: true };
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify(ok) });
  });
  await page.route("**/discovery/action-all", async (route) => {
    actionAllPosts.push(route.request().postDataJSON());
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ jobId: "job-abc", description: "Whisparr: discovery bulk action" }),
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

// Fail loudly if any native dialog (alert/confirm) ever appears — bulk + per-card feedback must never be one.
function forbidNativeDialogs(page) {
  page.on("dialog", (dialog) => {
    throw new Error(`unexpected native dialog: ${dialog.type()} "${dialog.message()}"`);
  });
}

test("discovery unmonitor search — per-card Unmonitor flips the pill (reverting on failure) and Search grabs once", async ({
  harness,
  baseUrl,
  page,
}, testInfo) => {
  forbidNativeDialogs(page);
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId, "seedCorpus should link at least one scene to a studio").not.toBeUndefined();

  const scenes = syntheticScenes(TOTAL, WANTED);
  const actionPosts = [];
  const actionAllPosts = [];
  const fails = { value: false };
  await routeDiscovery(page, { scenes, version: "v3", actionPosts, actionAllPosts, fails });

  await openMissingTab(page, baseUrl, studioId);

  // The wanted cards (status:"wanted") each expose an Unmonitor control; the not-added ones expose Monitor.
  await expect
    .poll(() => page.getByRole("button", { name: /Remove Scene .* from the wanted list/ }).count())
    .toBe(WANTED);

  // ---- (1) a wanted card's Unmonitor POSTs ONE {Op:"unmonitor"} + optimistically flips the pill ----
  const unmonitor1 = page.getByRole("button", { name: "Remove Scene 0001 from the wanted list" });
  await unmonitor1.click();
  await expect.poll(() => actionPosts.length).toBe(1);
  expect(actionPosts[0]).toMatchObject({ Op: "unmonitor", Kind: "studio", CoveEntityId: studioId, SourceId: "synthetic-0001" });
  // The card flips: its Unmonitor control is replaced by Monitor (the pill read Unmonitored), so one fewer wanted.
  await expect
    .poll(() => page.getByRole("button", { name: /Remove Scene .* from the wanted list/ }).count())
    .toBe(WANTED - 1);
  await page.screenshot({ path: testInfo.outputPath("after-card-unmonitor.png") });

  // ---- (2) a card's Search POSTs ONE {Op:"search"} (the sole grab); no native alert ----
  await page.getByRole("button", { name: "Search for Scene 0004 now" }).click();
  await expect.poll(() => actionPosts.filter((p) => p.Op === "search").length).toBe(1);
  expect(actionPosts.at(-1)).toMatchObject({ Op: "search", Kind: "studio", CoveEntityId: studioId, SourceId: "synthetic-0004" });

  // ---- (3) an induced 502 on Unmonitor REVERTS the optimistic flip (not a silent success) ----
  fails.value = true;
  const before = await page.getByRole("button", { name: /Remove Scene .* from the wanted list/ }).count();
  await page.getByRole("button", { name: "Remove Scene 0002 from the wanted list" }).click();
  await expect.poll(() => actionPosts.filter((p) => p.Op === "unmonitor").length).toBe(2);
  // The card returns to its wanted state (its Unmonitor control reappears) — the failure was not swallowed.
  await expect
    .poll(() => page.getByRole("button", { name: /Remove Scene .* from the wanted list/ }).count())
    .toBe(before);
});

test("discovery unmonitor search — the selection bar's Unmonitor + Search act over the selection via the bulk route", async ({
  harness,
  baseUrl,
  page,
}) => {
  forbidNativeDialogs(page);
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  const scenes = syntheticScenes(TOTAL, WANTED);
  const actionPosts = [];
  const actionAllPosts = [];
  const fails = { value: false };
  await routeDiscovery(page, { scenes, version: "v3", actionPosts, actionAllPosts, fails });

  await openMissingTab(page, baseUrl, studioId);
  // Scoped to the missing list: Cove's own entity cards carry their own "Select item" button, which an
  // unscoped match counts while this tab is still mounting.
  await expect
    .poll(() =>
      page
        .getByRole("list", { name: "Missing scenes" })
        .getByRole("button", { name: /^Select / })
        .count(),
    )
    .toBe(TOTAL);

  // Select three wanted cards → the bar appears with Unmonitor + Search.
  await page.getByRole("button", { name: "Select Scene 0001" }).click();
  await page.getByRole("button", { name: "Select Scene 0002" }).click();
  await page.getByRole("button", { name: "Select Scene 0003" }).click();
  await expect(page.getByText(/3 selected/i)).toBeVisible();

  // The bar's Unmonitor POSTs /discovery/action-all {Op:"unmonitor", SourceIds:[the selection]}.
  await page.getByRole("button", { name: "Unmonitor", exact: true }).click();
  await expect.poll(() => actionAllPosts.length).toBe(1);
  expect(actionAllPosts[0]).toMatchObject({ Op: "unmonitor", Kind: "studio", CoveEntityId: studioId });
  expect(actionAllPosts[0].SourceIds.slice().sort()).toEqual(["synthetic-0001", "synthetic-0002", "synthetic-0003"]);
  await expect(page.getByText(/selected/i)).toHaveCount(0); // the selection clears on enqueue

  // Re-select two and use the bar's Search → /discovery/action-all {Op:"search", SourceIds:[…]}.
  await page.getByRole("button", { name: "Select Scene 0004" }).click();
  await page.getByRole("button", { name: "Select Scene 0005" }).click();
  await page.getByRole("button", { name: "Search", exact: true }).click();
  await expect.poll(() => actionAllPosts.length).toBe(2);
  expect(actionAllPosts[1]).toMatchObject({ Op: "search", Kind: "studio", CoveEntityId: studioId });
  expect(actionAllPosts[1].SourceIds.slice().sort()).toEqual(["synthetic-0004", "synthetic-0005"]);
});

test("discovery unmonitor search — on v2 every per-scene verb is disabled with the shipped copy", async ({
  harness,
  baseUrl,
  page,
}) => {
  forbidNativeDialogs(page);
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId).not.toBeUndefined();

  const scenes = syntheticScenes(TOTAL, WANTED);
  const actionPosts = [];
  const actionAllPosts = [];
  const fails = { value: false };
  await routeDiscovery(page, { scenes, version: "v2", actionPosts, actionAllPosts, fails });

  await openMissingTab(page, baseUrl, studioId);

  // A wanted card on v2 still shows Unmonitor and Search, both disabled: each commands a scene-level
  // Whisparr row, which that generation has none of. The search verb that DOES survive on v2 is the
  // entity menu's "Search all monitored", not this per-scene one.
  const unmonitor1 = page.getByRole("button", { name: "Remove Scene 0001 from the wanted list" });
  await expect(unmonitor1).toBeVisible();
  await expect(unmonitor1).toBeDisabled();
  await expect(page.getByRole("button", { name: "Search for Scene 0001 now" })).toBeDisabled();

  // The bar mirrors it: both per-scene verbs disabled on v2.
  await page.getByRole("button", { name: "Select Scene 0001" }).click();
  await expect(page.getByText(/1 selected/i)).toBeVisible();
  await expect(page.getByRole("button", { name: "Unmonitor", exact: true })).toBeDisabled();
  await expect(page.getByRole("button", { name: "Search", exact: true })).toBeDisabled();
});

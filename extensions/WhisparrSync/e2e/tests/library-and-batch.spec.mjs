// Hermetic (Cove-only, no Whisparr container): the off-by-default library status toggle, the videos-list
// "Whisparr" batch menu, and the scene detail Whisparr panel rendering gracefully with no connection. The
// actual add/search actions hit Whisparr and belong to the env-gated container specs — this asserts
// PRESENCE + graceful rendering only.
import { test, expect, seedCorpus, routeUnconfigured } from "../lib/whisparrsync-fixtures.mjs";
import { VideosPage } from "@cove-extensions/e2e/pages/videos-page";
import { ScenePanel } from "../lib/pages/scene-panel.mjs";

test("the library Whisparr toggle is present and OFF by default", async ({ harness, baseUrl, page }) => {
  await seedCorpus({ container: harness.container, baseUrl });

  const videos = new VideosPage(page, baseUrl);
  await videos.goto();

  const toggle = await videos.waitForLibraryToggle();
  // Quiet by default: the toggle is off (aria-pressed="false"), nothing painted on the library yet.
  await expect(toggle).toHaveAttribute("aria-pressed", "false");
});

test("the videos-list Whisparr batch menu presents its six ordered actions", async ({
  harness,
  baseUrl,
  page,
}) => {
  await seedCorpus({ container: harness.container, baseUrl });

  const videos = new VideosPage(page, baseUrl);
  await videos.goto();
  await videos.selectFirstCards(2);
  await videos.openWhisparrBatchMenu();

  // Menu presence: the chooser is a role="menu" with the six ordered role="menuitem" rows. Each label is
  // pinned at its real index (mirrors BATCH_MENU_ITEMS in common/lib/sceneActionsLogic) so a future reorder
  // or drop of the Monitor/Unmonitor items (added in 524fc0c/9b67492) can't slip past a bare count check.
  await expect(videos.batchMenu).toBeVisible();
  const items = videos.batchMenuItems();
  await expect(items).toHaveCount(6);
  await expect(items.nth(0)).toContainText("Add to Whisparr");
  await expect(items.nth(1)).toContainText("Monitor");
  await expect(items.nth(2)).toContainText("Unmonitor");
  await expect(items.nth(3)).toContainText("Search now");
  await expect(items.nth(4)).toContainText("Search for upgrades");
  await expect(items.nth(5)).toContainText("Exclude from Whisparr");
});

test("the scene detail Whisparr tab renders a status gracefully with no connection", async ({
  harness,
  baseUrl,
  api,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const [firstScene] = seeded.values();

  // State the unconfigured premise rather than inheriting it: options are one blob per instance and the
  // harness is worker-scoped, so a sibling spec saving a connection would otherwise make this assert
  // against a CONFIGURED panel and time out waiting for copy that will never render.
  await routeUnconfigured(page);

  const scene = new ScenePanel(page, baseUrl);
  await scene.gotoVideo(firstScene.coveVideoId);
  await scene.openWhisparrTab();

  // Graceful, not a crash/blank: the panel renders a non-empty Whisparr status message even unconfigured.
  const text = await scene.statusText();
  expect(text.trim().length).toBeGreaterThan(0);
  expect(text).toMatch(/Whisparr|StashDB/);

  // And the underlying scene-detail read answers (200) rather than 500-ing without a connection.
  const detail = await api.post(`/api/extensions/com.alextomas955.whisparrsync/scene-detail`, {
    CoveId: firstScene.coveVideoId,
  });
  expect(detail.status).toBe(200);
});

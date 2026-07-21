// Hermetic (Cove-only, no Whisparr container): the off-by-default library status toggle (VIEW-01), the
// videos-list "Whisparr" batch menu (DSN-01), and the scene detail Whisparr panel rendering gracefully with
// no connection. The actual add/search actions hit Whisparr and belong to the env-gated plan-03 specs — this
// asserts PRESENCE + graceful rendering only.
import { test, expect, seedCorpus } from "../lib/whisparrsync-fixtures.mjs";
import { VideosListPage } from "../lib/pages/videos-list-page.mjs";
import { ScenePanel } from "../lib/pages/scene-panel.mjs";

test("the library Whisparr toggle is present and OFF by default", async ({ harness, baseUrl, page }) => {
  await seedCorpus({ container: harness.container, baseUrl });

  const videos = new VideosListPage(page, baseUrl);
  await videos.goto();

  const toggle = videos.libraryToggle();
  await expect(toggle).toBeVisible();
  // VIEW-01: quiet by default — the toggle is off (aria-pressed="false"), nothing painted on the library yet.
  await expect(toggle).toHaveAttribute("aria-pressed", "false");
});

test("the videos-list Whisparr batch menu presents its four ordered actions", async ({
  harness,
  baseUrl,
  page,
}) => {
  await seedCorpus({ container: harness.container, baseUrl });

  const videos = new VideosListPage(page, baseUrl);
  await videos.goto();
  await videos.selectFirstCards(2);
  await videos.openWhisparrBatchMenu();

  // DSN-01 presence: the chooser is a role="menu" with the four ordered role="menuitem" rows.
  await expect(videos.batchMenu).toBeVisible();
  await expect(videos.batchMenuItems()).toHaveCount(4);
  await expect(videos.batchMenuItems().nth(0)).toContainText("Add to Whisparr");
  await expect(videos.batchMenuItems().nth(3)).toContainText("Exclude from Whisparr");
});

test("the scene detail Whisparr tab renders a status gracefully with no connection", async ({
  harness,
  baseUrl,
  api,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const [firstScene] = seeded.values();

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

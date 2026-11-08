// Verifies Renamer's AutoRenamerOnUpdate hook end-to-end through the real UI: enabling "Auto-
// rename on update" in the settings panel, then editing a video's title via its real Edit tab,
// must rename the file automatically - with no explicit "Rename selected" action from the user.
//
// AutoRenamerOnUpdate is a global extension setting, so the test takes `restoredOptions`.
import { test, expect, seedVideo } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";
import { VideoDetailPage } from "../lib/pages/video-detail-page.mjs";
import { VideosPage } from "@cove-extensions/e2e/pages/videos-page";
import { assertRenamedTo } from "../lib/rename-assertions.mjs";

test("enabling Auto-rename on update and editing a title through the UI renames the file automatically", async ({
  page,
  harness,
  baseUrl,
  api,
  restoredOptions: _restoredOptions,
}) => {
  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();
  await settingsPage.enableAutoRenameOnUpdate();
  // A "$title"-only template over a safe title makes the auto-produced name deterministic, so the
  // exact resulting basename can be asserted rather than merely "the path changed".
  await settingsPage.setFilenameTemplate("$title");
  await settingsPage.save();

  const video = await seedVideo({ container: harness.container, baseUrl });
  const originalPath = video.files[0].path;

  const title = "Auto Rename Test Title";
  const detailPage = new VideoDetailPage(page, baseUrl);
  await detailPage.goto(video.id);
  await detailPage.openEditTab();
  await detailPage.setTitle(title);

  // No "Rename selected" click anywhere in this test - the hook alone must produce the rename.
  await assertRenamedTo({
    api,
    container: harness.container,
    videoId: video.id,
    expectedBasename: `${title}.mp4`,
    originalPath,
  });
  const afterEdit = await api.get(`/api/videos/${video.id}`).then((r) => r.json);
  expect(afterEdit.title).toBe(title);

  // And the grid reflects it too, same as a real user would see without refreshing anything special.
  // The card is found by the name its own link carries, so a restyle of the grid cannot move this
  // assertion onto a different element than the one a user reads. Cove names that link after the
  // card's title and falls back to the filename only for a video that has none, so the title is what
  // this card reads once the edit above has landed.
  const videos = new VideosPage(page, baseUrl);
  await videos.goto();
  await expect(
    page.getByRole("link", { name: `Open video ${title}` }),
    "the grid did not show the edited video",
  ).toHaveCount(1);
});

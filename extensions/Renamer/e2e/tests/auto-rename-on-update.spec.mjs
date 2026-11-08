// Renamer's auto-rename-on-update hook through the UI: with the setting on, editing a video's
// title in its Edit tab renames the file with no "Rename selected" action from the user.
//
// The setting is global to the extension, so the test takes `restoredOptions`.
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
  // A title of letters and spaces only passes through the sanitizer unchanged, so the exact
  // resulting basename can be asserted rather than only "the path changed".
  await settingsPage.setFilenameTemplate("$title");
  await settingsPage.save();

  const video = await seedVideo({ container: harness.container, baseUrl });
  const originalPath = video.files[0].path;

  const title = "Auto Rename Test Title";
  const detailPage = new VideoDetailPage(page, baseUrl);
  await detailPage.goto(video.id);
  await detailPage.openEditTab();
  await detailPage.setTitle(title);

  await assertRenamedTo({
    api,
    container: harness.container,
    videoId: video.id,
    expectedBasename: `${title}.mp4`,
    originalPath,
  });
  const afterEdit = await api.get(`/api/videos/${video.id}`).then((r) => r.json);
  expect(afterEdit.title).toBe(title);

  // Cove names a card's link after the title and falls back to the filename only for a video that
  // has none, so this card reads as the title once the edit above has landed.
  const videos = new VideosPage(page, baseUrl);
  await videos.goto();
  await expect(
    page.getByRole("link", { name: `Open video ${title}` }),
    "the grid did not show the edited video",
  ).toHaveCount(1);
});

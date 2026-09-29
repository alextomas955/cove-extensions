// Core rename, undo and live-preview coverage driven through the real UI (Videos grid + Renamer
// settings panel), not the REST API: "Rename selected" raises a native confirm() with the real
// computed preview text, and "Undo last rename" opens an in-app (React) confirm modal, not a native
// dialog. See lib/pages/ for the Page Object Model.
// `@smoke` marks the tests spanning install -> enable -> rename -> undo, one per contract in the
// cheapest file that carries it; the install itself is proven by the harness, which refuses to hand
// over an instance where the extension is not enabled. `ci.yml` selects them with `--grep @smoke` on a
// leg whose role is newest-GA and nothing else, and states there why that leg asks only this much. It
// is a selection, never a tier: every one of these runs in the full suite too.
import { test, expect, seedVideo } from "../lib/renamer-fixtures.mjs";
import { VideosPage } from "@cove-extensions/e2e/pages/videos-page";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";
import { assertRenamedTo, assertRestoredTo } from "../lib/rename-assertions.mjs";

test("editing the filename template updates the live preview and enables Save", async ({
  page,
  baseUrl,
}) => {
  const errors = [];
  page.on("pageerror", (err) => errors.push(err.message));

  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();

  await expect(settingsPage.filenameTemplateInput).toBeVisible();
  await settingsPage.setFilenameTemplate("$title-e2e-ui-marker");

  await expect(settingsPage.liveVideoSampleCard()).toContainText("e2e-ui-marker", {
    timeout: 10_000,
  });
  await expect(settingsPage.unsavedChangesIndicator).toBeVisible();
  await expect(settingsPage.saveChangesButton).toBeVisible();

  expect(errors, `Unexpected console errors: ${errors.join("; ")}`).toEqual([]);
});

test(
  "clicking Save changes persists a settings edit across a page reload",
  { tag: "@smoke" },
  async ({ page, baseUrl, restoredOptions: _restoredOptions }) => {
    const settingsPage = new RenamerSettingsPage(page, baseUrl);
    await settingsPage.goto();

    await settingsPage.setFilenameTemplate("$title-e2e-save-marker");
    await settingsPage.save();

    await page.reload();
    await expect(settingsPage.filenameTemplateInput).toHaveValue("$title-e2e-save-marker");
  },
);

test(
  "selecting a video and clicking Rename selected renames it on disk and in the DB; Undo restores it",
  { tag: "@smoke" },
  async ({ page, harness, baseUrl, api, restoredOptions: _restoredOptions }) => {
    const video = await seedVideo({ container: harness.container, baseUrl });
    const originalFilename = video.files[0].path.split("/").pop();
    const originalPath = video.files[0].path;

    // A "$title"-only template over a safe title (letters + spaces only, which the sanitizer passes
    // through unchanged) makes the resulting name deterministic and independent of date/resolution
    // metadata, so the exact resulting basename can be asserted, not merely "the path changed".
    const title = "Core Path Rename Test";
    const expectedBasename = `${title}.mp4`;

    const settingsPage = new RenamerSettingsPage(page, baseUrl);
    await settingsPage.goto();
    await settingsPage.setFilenameTemplate("$title");
    await settingsPage.save();

    const videosPage = new VideosPage(page, baseUrl);
    await videosPage.goto();
    // Select the card by its filename before setting a Title: the grid card's accessible name follows
    // the item's title once one is set, so selecting first keeps the filename-based lookup valid.
    await videosPage.selectCard(originalFilename);

    const setTitle = await api.put(`/api/videos/${video.id}`, { Title: title });
    expect(setTitle.ok).toBe(true);

    const dialogMessages = await videosPage.renameSelected();
    expect(dialogMessages[0]).toContain(originalFilename);

    await assertRenamedTo({
      api,
      container: harness.container,
      videoId: video.id,
      expectedBasename,
      originalPath,
    });

    await settingsPage.goto();
    await settingsPage.undoLastRename();

    await assertRestoredTo({ api, container: harness.container, videoId: video.id, originalPath });

    // The grid labels a card by its title once one is set.
    await videosPage.goto();
    await expect(page.getByRole("link", { name: `Open video ${title}` })).toBeVisible();
  },
);

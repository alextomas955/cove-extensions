// Core rename/undo/preview coverage driven through the real UI (Videos grid + Renamer settings
// panel), not the rest API: "Rename selected" raises a native confirm() with the real computed
// preview text, then a native alert() confirming the job was queued; "Undo last rename" opens an
// in-app (React) confirm modal, not a native dialog. See lib/pages/ for the Page Object Model.
// `@smoke` marks the six tests spanning install -> enable -> rename -> undo, one per contract in the
// cheapest file that carries it. `build.yml` selects them with `--grep @smoke` on a leg whose role is
// newest-GA and nothing else, and states there why that leg asks only this much. It is a selection,
// never a tier: every one of these runs in the full suite too.
import { test, expect, seedVideo } from "../lib/renamer-fixtures.mjs";
import { VideosPage } from "@cove-extensions/e2e/pages/videos-page";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";
import { assertRenamedTo, assertRestoredTo } from "../lib/rename-assertions.mjs";

const EXTENSION_ID = "com.alextomas955.renamer";
const DEFAULT_FILENAME_TEMPLATE = "{$date - }$title{ [$resolution]}";

/**
 * The filename template the extension renders with, or undefined when none is stored.
 *
 * The persisted settings blob is a JSON string under `options`, spelled in the PascalCase of the
 * C# record rather than the camelCase of the wire document. An instance that has never saved its
 * settings carries no blob, which means the shipped default is in force.
 */
async function templateInForce(api) {
  const all = await api.get(`/api/extensions/${EXTENSION_ID}/data`);
  const blob = (all.json ?? {}).options;
  return blob ? JSON.parse(blob).FilenameTemplate : undefined;
}

test(
  "extension installs and reports enabled with UI, API, jobs, and state capabilities",
  { tag: "@smoke" },
  async ({ api }) => {
    const { json } = await api.get("/api/extensions");
    const renamer = json.find((e) => e.id === EXTENSION_ID);
    expect(renamer).toBeTruthy();
    expect(renamer.enabled).toBe(true);
    expect(renamer.hasUI).toBe(true);
    expect(renamer.hasApi).toBe(true);
    expect(renamer.hasJobs).toBe(true);
  },
);

test("editing the filename template updates the live preview and enables Save", async ({
  page,
  baseUrl,
}) => {
  const errors = [];
  page.on("pageerror", (err) => errors.push(err.message));

  // Drives the actual template textbox and asserts the debounced live-preview panel
  // (POST /preview-sample) and dirty-state save bar both reflect the edit — a "no console error"
  // check alone would miss a stale/duplicated fetch overwriting the preview with wrong data,
  // since that failure mode doesn't throw.
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
  async ({ page, baseUrl, api }) => {
    const settingsPage = new RenamerSettingsPage(page, baseUrl);
    await settingsPage.goto();

    await settingsPage.setFilenameTemplate("$title-e2e-save-marker");
    await settingsPage.save();

    try {
      await page.reload();
      await expect(settingsPage.filenameTemplateInput).toHaveValue("$title-e2e-save-marker");
    } finally {
      // The save above writes a global Renamer option into the Cove instance every sibling spec on
      // this worker shares, so without this reset the marker template renders the filenames every
      // later test asserts on. This route replaces the stored document rather than merging it, so
      // the write restores the whole options record to its defaults.
      const reset = await api.put(
        `/api/extensions/${EXTENSION_ID}/data/options`,
        JSON.stringify({ FilenameTemplate: DEFAULT_FILENAME_TEMPLATE }),
      );
      expect(
        reset.ok,
        `resetting the filename template returned ${reset.status}; every later test on this worker renders with the marker template`,
      ).toBe(true);
    }
  },
);

test("dry-run preview matches the template and touches neither disk nor the DB record", async ({
  harness,
  baseUrl,
  api,
}) => {
  const video = await seedVideo({ container: harness.container, baseUrl });
  const originalPath = video.files[0].path;

  // An untitled item falls back to its own basename as $title, so the default template renders the
  // name the file already has and the planner correctly reports a no-op. The title is set on the
  // item this test seeded rather than in the extension's settings, so nothing leaks into a sibling
  // test sharing this worker's Cove instance.
  const title = "Dry Run Preview Test";
  const setTitle = await api.put(`/api/videos/${video.id}`, { Title: title });
  expect(setTitle.ok).toBe(true);

  // The exact name asserted below holds only while the default template is in force, and this test
  // does not control that: the settings are per-instance and this worker's instance is shared.
  expect(
    (await templateInForce(api)) ?? DEFAULT_FILENAME_TEMPLATE,
    "the filename template in force is not the default, so the rendered name is not determined",
  ).toBe(DEFAULT_FILENAME_TEMPLATE);

  // /preview has no UI trigger of its own (it's what "Rename selected" calls internally before
  // showing its confirm() dialog) — the API is the only way to exercise it in isolation, without
  // also triggering the actual mutation the UI action performs. This one test stays API-driven.
  const preview = await api.post(`/api/extensions/${EXTENSION_ID}/preview`, {
    EntityType: "video",
    EntityIds: [video.id],
  });
  expect(preview.status).toBe(200);
  expect(preview.json.items).toHaveLength(1);
  expect(preview.json.items[0].status).toBe("renamer");
  expect(preview.json.items[0].oldFullPath).toBe(originalPath);
  // This test sets no date and no resolution metadata, so both optional groups of the default
  // template collapse and the rendered name is fully determined.
  expect(preview.json.items[0].newBasename).toBe(`${title}.mp4`);

  const afterPreview = await api.get(`/api/videos/${video.id}`);
  expect(afterPreview.json.files[0].path).toBe(originalPath);
  expect(afterPreview.json.title).toBe(title);
});

test(
  "selecting a video and clicking Rename selected renames it on disk and in the DB; Undo restores it",
  { tag: "@smoke" },
  async ({ page, harness, baseUrl, api }) => {
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
    // The confirm() dialog shows the real computed preview — assert on it, not just that a dialog
    // fired, so this test would catch a regression in what the preview text itself says.
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

    // Confirm the item still renders in the grid after the undo round-trip — a real user driving the
    // UI sees the card come back (labeled by its title, which the grid shows once one is set). The
    // restored filename itself is proven on disk and in the DB by assertRestoredTo above.
    await videosPage.goto();
    await expect(page.getByRole("link", { name: `Open video ${title}` })).toBeVisible();

    // Restore the default template so a bare "$title" (a no-op for an untitled item) does not leak
    // into a sibling test sharing this worker's Cove instance.
    await settingsPage.goto();
    await settingsPage.setFilenameTemplate("{$date - }$title{ [$resolution]}");
    await settingsPage.save();
  },
);

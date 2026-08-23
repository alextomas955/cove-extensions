// Verifies Renamer's AutoRenamerOnUpdate hook end-to-end through the real UI: enabling "Auto-
// rename on update" in the settings panel, then editing a video's title via its real Edit tab,
// must rename the file automatically — with NO explicit "Rename selected" action from the user.
//
// The hook is reachable two ways, and they have DIFFERENT host plumbing behind them: editing one item
// goes through VideosController.Update, which publishes its own entity event, while editing several at
// once goes through /videos/bulk, which relies entirely on EntityEventFilter. For a long time only the
// first was covered and the second silently did nothing (issue #108). Both live here now, so the
// cheaper single-item case can never again stand in for the whole feature.
//
// Uses `isolatedTest` — its OWN harness instance PER TEST, not the shared per-worker one:
// AutoRenamerOnUpdate is a global extension setting that would leak into every other test sharing
// that worker's instance once enabled, silently changing their behaviour (the collision test, for
// one, relies on the default template/no-auto-rename state).
import {
  isolatedTest as test,
  expect,
  seedVideo,
  createApiClient,
  pollUntil,
} from "../lib/renamer-fixtures.mjs";
import { imageAtLeastVersion, resolveCoveImage } from "@cove-extensions/e2e/harness";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";
import { VideoDetailPage } from "../lib/pages/video-detail-page.mjs";
import { assertRenamedTo, basename } from "../lib/rename-assertions.mjs";

const EXTENSION_ID = "com.alextomas955.renamer";

test("enabling Auto-rename on update and editing a title through the UI renames the file automatically", async ({
  page,
  isolatedHarness,
}) => {
  const baseUrl = isolatedHarness.baseUrl;
  const api = createApiClient(baseUrl, isolatedHarness.token);

  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();
  await settingsPage.enableAutoRenameOnUpdate();
  // A "$title"-only template over a safe title makes the auto-produced name deterministic, so the
  // EXACT resulting basename can be asserted rather than merely "the path changed".
  await settingsPage.setFilenameTemplate("$title");
  await settingsPage.save();

  const video = await seedVideo({ container: isolatedHarness.container, baseUrl });
  const originalPath = video.files[0].path;

  const title = "Auto Rename Test Title";
  const detailPage = new VideoDetailPage(page, baseUrl);
  await detailPage.goto(video.id);
  await detailPage.openEditTab();
  await detailPage.setTitle(title);

  // No "Rename selected" click anywhere in this test — the hook alone must produce the rename.
  await assertRenamedTo({
    api,
    container: isolatedHarness.container,
    videoId: video.id,
    expectedBasename: `${title}.mp4`,
    originalPath,
  });
  const afterEdit = await api.get(`/api/videos/${video.id}`).then((r) => r.json);
  expect(afterEdit.title).toBe(title);

  // And the grid reflects it too, same as a real user would see without refreshing anything special.
  await page.goto(`${baseUrl}/videos`);
  await page.waitForLoadState("networkidle");
  const filenames = await page.locator("main p").allTextContents();
  expect(filenames).toContain(title);
});

test("with Auto-rename on update left OFF (the default), editing a title does not rename the file", async ({
  page,
  isolatedHarness,
}) => {
  const baseUrl = isolatedHarness.baseUrl;
  const api = createApiClient(baseUrl, isolatedHarness.token);

  // No settings change here — AutoRenamerOnUpdate defaults to false. This is the negative-path
  // counterpart to the test above: confirms the hook is genuinely opt-in, not just untested.
  const video = await seedVideo({ container: isolatedHarness.container, baseUrl });
  const originalPath = video.files[0].path;

  const detailPage = new VideoDetailPage(page, baseUrl);
  await detailPage.goto(video.id);
  await detailPage.openEditTab();
  await detailPage.setTitle("Should Not Trigger Rename");

  // Give the (absent) hook the same window the positive test needs to prove it real — a fixed
  // wait is appropriate here specifically because the assertion is "nothing happened," which
  // pollUntil's early-exit-on-success shape can't express (there's no success condition to poll for).
  await page.waitForTimeout(5_000);

  const afterEdit = await api.get(`/api/videos/${video.id}`).then((r) => r.json);
  expect(afterEdit.files[0].path).toBe(originalPath);
  expect(afterEdit.title).toBe("Should Not Trigger Rename");
});

// ── A title-less item, and a destination that actually relocates ─────────────────────────────────
//
// Every test above sets a title and leaves the folder template blank, which is the one configuration
// that is a fixed point for free. That fixture choice is not a coincidence here — the same two values
// were held constant at all three tiers, which is why the reported runaway (each pass adding one more
// directory and one more copy of the template's decorations, until FullPathMax refused the path) was
// invisible to the whole suite. So this varies exactly those two.
//
// What THIS tier proves that the L0 invariant cannot, stated rather than assumed — the invariant
// (`PlanFixedPointTests`, 70 cells) quantifies convergence far more widely than one spec ever could,
// and none of it is repeated here:
//
//   * The anchor is Cove's own library path, read from the host's `CoveConfiguration` through the
//     extension-overlay container. Nothing in the C# suite builds that container — the in-process tier
//     registers the configuration itself — so "the host really hands this extension its library paths"
//     is structurally invisible below this line, and every source-confined move rests on it.
//   * The loop is real. L0 MODELS the commit between two plans (new basename, new parent, recorded
//     title); here the host raises its own `video.updated` after the executor's save and the hook
//     re-enters on it, which is the mechanism that turned one edit into eight renames.
//   * The move lands on a real filesystem, and the derived title lands in Cove's own record through
//     Cove's own save — the write that makes the title a one-time derivation instead of a per-pass one.

test("an untitled item with a folder template moves once, and a later edit does not move it again", async ({
  isolatedHarness,
}) => {
  const baseUrl = isolatedHarness.baseUrl;
  const api = createApiClient(baseUrl, isolatedHarness.token);

  // FolderRoot is left at its default — "the file's own library path" — which is the arrangement that
  // used to measure from the file's CURRENT parent and so nested one level per pass. The literal in
  // the filename template is what makes the second defect visible: with the title derived from the
  // basename every pass, "renamed - " was wrapped around its own output each time.
  const put = await api.put(
    `/api/extensions/${EXTENSION_ID}/data/options`,
    JSON.stringify({
      AutoRenamerOnUpdate: true,
      FilenameAsTitle: true,
      FilenameTemplate: "renamed - $title",
      FolderTemplate: "sorted",
    }),
  );
  expect(put.ok).toBe(true);

  // Read back, for the reason the bulk test states: an unapplied setting and a broken hook produce the
  // same "nothing moved", and the first would make this prove nothing.
  const readBack = await api.get(`/api/extensions/${EXTENSION_ID}/data`);
  const raw = readBack.json?.options;
  const applied = typeof raw === "string" ? JSON.parse(raw) : (raw ?? {});
  expect(applied.FilenameAsTitle, "FilenameAsTitle did not persist").toBe(true);
  expect(applied.FolderTemplate, "FolderTemplate did not persist").toBe("sorted");

  const video = await seedVideo({
    container: isolatedHarness.container,
    baseUrl,
    destName: "e2e-untitled-item.mp4",
  });
  const originalPath = video.files[0].path;
  expect(originalPath).toBe("/data/e2e-untitled-item.mp4");

  // The premise, asserted rather than assumed: a host that titled its own imports would make every
  // assertion below a test of the titled path wearing this test's name.
  const seeded = await api.get(`/api/videos/${video.id}`).then((r) => r.json);
  expect(
    seeded.title,
    "the seeded item already carries a title, so nothing here is title-less",
  ).toBeFalsy();

  // Any edit raises video.updated; this one deliberately does not touch the title.
  expect((await api.put(`/api/videos/${video.id}`, { details: "first edit" })).ok).toBe(true);

  const afterFirst = await pollUntil(
    () => api.get(`/api/videos/${video.id}`).then((r) => r.json),
    (v) => v.files[0].path !== originalPath,
    { label: `video ${video.id} to be moved by the auto-rename hook` },
  );
  const movedPath = afterFirst.files[0].path;

  // The folder is the whole first defect: the destination is the library path plus the template, so it
  // is "/data/sorted" and stays "/data/sorted" however many times the hook re-enters. Nesting shows up
  // here as "/data/sorted/sorted".
  expect(movedPath.slice(0, movedPath.lastIndexOf("/"))).toBe("/data/sorted");

  // …and the name is the second. The template's literal appears exactly once; wrapping it around its
  // own output is what produced "Ruby Monroe.Ruby Monroe.…" in the report.
  const movedName = basename(movedPath);
  expect(
    movedName.match(/renamed - /g)?.length,
    `basename "${movedName}" repeats the template`,
  ).toBe(1);

  expect((await isolatedHarness.container.exec(["test", "-f", movedPath])).exitCode).toBe(0);
  expect((await isolatedHarness.container.exec(["test", "-f", originalPath])).exitCode).not.toBe(0);

  // The derived title reached Cove's own record, and it is the ORIGINAL stem rather than the name the
  // template produced — which is what makes the derivation happen once instead of every pass.
  expect(
    afterFirst.title,
    "no title was recorded, so the fallback still re-derives every pass",
  ).toBeTruthy();
  expect(afterFirst.title).not.toContain("renamed - ");

  // A second, unrelated edit. The hook re-enters and must find nothing to do.
  expect((await api.put(`/api/videos/${video.id}`, { details: "second edit" })).ok).toBe(true);

  // A fixed wait, for the reason the OFF-by-default test above states: "nothing happened" has no
  // success condition to poll for, and an early exit would report a runaway as settled.
  await new Promise((resolve) => setTimeout(resolve, 8_000));

  const afterSecond = await api.get(`/api/videos/${video.id}`).then((r) => r.json);
  expect(
    afterSecond.files[0].path,
    "a second edit moved the file again — the plan is not a fixed point",
  ).toBe(movedPath);
});

// ── Multi-item edits ─────────────────────────────────────────────────────────────────────────────
//
// Cove publishes entity events for bulk mutations from 1.2.0 onward. Before that, /videos/bulk saves
// the rows and raises nothing, so the hook is never invoked and cannot be — an extension cannot
// observe an event the host does not publish.
//
// This therefore asserts a HOST CAPABILITY the feature depends on, and is skipped — loudly, never
// silently — on a host below that floor. The image is asked of the harness rather than written down
// here, so the skip keys on the host the run actually booted, including the tag-only form a CI version
// leg supplies. (Issue #108.)
const BULK_EVENTS_SINCE = "1.2.0";
const HOST_IMAGE = resolveCoveImage();
const HOST_PUBLISHES_BULK_EVENTS = imageAtLeastVersion(HOST_IMAGE, BULK_EVENTS_SINCE);
const BULK_COUNT = 6;

test.describe("multi-item edits", () => {
  test.skip(
    !HOST_PUBLISHES_BULK_EVENTS,
    `host ${HOST_IMAGE} is below Cove ${BULK_EVENTS_SINCE}, so /videos/bulk publishes no entity ` +
      `events and the hook cannot fire. A host limitation, not a Renamer defect (issue #108).`,
  );

  test("setting Organized on several videos at once renames every one of them, not just one", async ({
    isolatedHarness,
  }) => {
    const baseUrl = isolatedHarness.baseUrl;
    const api = createApiClient(baseUrl, isolatedHarness.token);

    // The reporter's configuration: auto-rename on update, gated to organized items only. "$title"
    // alone makes each expected basename exact rather than merely "the path changed".
    const put = await api.put(
      `/api/extensions/${EXTENSION_ID}/data/options`,
      JSON.stringify({
        AutoRenamerOnUpdate: true,
        OnlyOrganized: true,
        FilenameTemplate: "$title",
      }),
    );
    expect(put.ok).toBe(true);

    // Read the options back. Without this a 0-renamed result is ambiguous — an unapplied setting and
    // a broken hook look identical, and the first would make the whole test prove nothing.
    // NOTE the route: the host exposes PUT `{id}/data/{key}` but only GET `{id}/data` (every key at
    // once). Asking for a single key returns the SPA shell, which parses to {} and reads as "nothing
    // persisted" no matter what was stored.
    const readBack = await api.get(`/api/extensions/${EXTENSION_ID}/data`);
    const raw = readBack.json?.options;
    const applied = typeof raw === "string" ? JSON.parse(raw) : (raw ?? {});
    expect(applied.AutoRenamerOnUpdate, "AutoRenamerOnUpdate did not persist").toBe(true);
    expect(applied.OnlyOrganized, "OnlyOrganized did not persist").toBe(true);

    // CONTROL: one video, organized through the per-entity route. If this does not rename, the hook
    // is off and the bulk result below says nothing about bulk.
    const control = await seedVideo({ container: isolatedHarness.container, baseUrl });
    const controlOriginal = control.files[0].path;
    await api.put(`/api/videos/${control.id}`, { title: "Control Single Item" });
    await api.put(`/api/videos/${control.id}`, { organized: true });
    const controlDeadline = Date.now() + 45_000;
    let controlRenamed = false;
    while (Date.now() < controlDeadline && !controlRenamed) {
      const now = await api.get(`/api/videos/${control.id}`).then((r) => r.json);
      controlRenamed = now?.files?.[0]?.path !== controlOriginal;
      if (!controlRenamed) await new Promise((r) => setTimeout(r, 2_000));
    }
    expect(
      controlRenamed,
      "the single-item path did not rename either — the hook is off, so the bulk result proves nothing",
    ).toBe(true);

    // Seed the batch and title each while still UNorganized. Each title edit raises its own
    // video.updated, but the only-organized gate skips it, so the bulk flip below is the sole trigger.
    const videos = [];
    for (let i = 0; i < BULK_COUNT; i++) {
      const seeded = await seedVideo({ container: isolatedHarness.container, baseUrl });
      const title = `Bulk Organized Item ${i + 1}`;
      expect((await api.put(`/api/videos/${seeded.id}`, { title })).ok).toBe(true);
      videos.push({ id: seeded.id, title, originalPath: seeded.files[0].path });
    }
    for (const v of videos) {
      const before = await api.get(`/api/videos/${v.id}`).then((r) => r.json);
      expect(before.files[0].path, `video ${v.id} renamed before being organized`).toBe(
        v.originalPath,
      );
    }

    // The reported action: one bulk edit setting Organized on the whole selection.
    const bulk = await api.post(`/api/videos/bulk`, {
      Ids: videos.map((v) => v.id),
      Organized: true,
    });
    expect(bulk.ok, `bulk update failed: ${bulk.status} ${bulk.text}`).toBe(true);

    // Poll the whole set rather than each in turn, so a slow-but-correct fan-out is not a failure.
    const deadline = Date.now() + 60_000;
    let renamed = [];
    while (Date.now() < deadline) {
      renamed = [];
      for (const v of videos) {
        const now = await api.get(`/api/videos/${v.id}`).then((r) => r.json);
        if (now?.files?.[0]?.path !== v.originalPath) renamed.push(v.id);
      }
      if (renamed.length === videos.length) break;
      await new Promise((r) => setTimeout(r, 2_000));
    }

    expect(
      renamed.length,
      `only ${renamed.length} of ${videos.length} were renamed after one bulk Organized edit`,
    ).toBe(videos.length);

    for (const v of videos) {
      const now = await api.get(`/api/videos/${v.id}`).then((r) => r.json);
      expect(
        now.files[0].path.endsWith(`/${v.title}.mp4`),
        `video ${v.id} at ${now.files[0].path}`,
      ).toBe(true);
    }
  });
});

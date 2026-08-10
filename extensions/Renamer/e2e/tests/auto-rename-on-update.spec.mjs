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
// Uses its OWN harness instance PER TEST (same pattern as extension-lifecycle.spec.mjs), NOT the
// shared per-worker harness: AutoRenamerOnUpdate is a global extension setting that would leak
// into every other test sharing that worker's instance once enabled, silently changing their
// behavior (e.g. the collision test relies on the default template/no-auto-rename state).
import { test as base, expect } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { seedVideo } from "@cove-extensions/e2e/seed-media";
import { RENAMER_EXTENSION } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";
import { VideoDetailPage } from "../lib/pages/video-detail-page.mjs";
import { assertRenamedTo } from "../lib/rename-assertions.mjs";

const test = base.extend({
  isolatedHarness: [
    async ({}, use) => {
      const isolatedHarness = await startHarness();
      isolatedHarness.owner = await isolatedHarness.bootstrapOwner();
      await isolatedHarness.installExtension(RENAMER_EXTENSION);
      await use(isolatedHarness);
      await isolatedHarness.stop();
    },
    { scope: "test" },
  ],
});

async function callApi(baseUrl, method, path, body) {
  const res = await fetch(`${baseUrl}${path}`, {
    method,
    headers: body ? { "Content-Type": "application/json" } : undefined,
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let json;
  try {
    json = text ? JSON.parse(text) : undefined;
  } catch {
    json = undefined;
  }
  return { status: res.status, ok: res.ok, json, text };
}

test("enabling Auto-rename on update and editing a title through the UI renames the file automatically", async ({
  page,
  isolatedHarness,
}) => {
  const baseUrl = isolatedHarness.baseUrl;
  const api = { get: (p) => callApi(baseUrl, "GET", p) };

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
  const api = { get: (p) => callApi(baseUrl, "GET", p) };

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

// ── Multi-item edits ─────────────────────────────────────────────────────────────────────────────
//
// Cove publishes entity events for bulk mutations only since `ca14830` (2026-08-02), which ships in
// nightly and in no released tag. Without it /videos/bulk saves the rows and raises nothing, so the
// hook is never invoked and cannot be — an extension cannot observe an event the host does not
// publish. Measured, one variable: cove-app:1.1.0 renamed 0 of 6, cove-app:nightly renamed 6 of 6.
//
// This therefore asserts a HOST CAPABILITY the feature depends on, and is skipped — loudly, never
// silently — on a host known to lack it. When the repo's pinned image moves to a release carrying
// ca14830 the skip stops firing and the assertion starts running, with no edit here. (Issue #108.)
const HOST_IMAGE = process.env.COVE_E2E_IMAGE ?? "ghcr.io/yourcove/cove-app:1.1.0";
const HOST_PUBLISHES_BULK_EVENTS = !/:(0\.|1\.0|1\.1\.0)/.test(HOST_IMAGE);
const EXTENSION_ID = "com.alextomas955.renamer";
const BULK_COUNT = 6;

/** Authenticated variant of `callApi` above — the writes below answer 401 without a bearer token. */
async function callApiAs(baseUrl, token, method, path, body) {
  const res = await fetch(`${baseUrl}${path}`, {
    method,
    headers: {
      ...(body ? { "Content-Type": "application/json" } : {}),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let json;
  try {
    json = text ? JSON.parse(text) : undefined;
  } catch {
    json = undefined;
  }
  return { status: res.status, ok: res.ok, json, text };
}

test.describe("multi-item edits", () => {
  test.skip(
    !HOST_PUBLISHES_BULK_EVENTS,
    `host ${HOST_IMAGE} predates Cove ca14830, so /videos/bulk publishes no entity events — the hook ` +
      `cannot fire. A host limitation, not a Renamer defect (issue #108).`,
  );

  test("setting Organized on several videos at once renames every one of them, not just one", async ({
    isolatedHarness,
  }) => {
    const baseUrl = isolatedHarness.baseUrl;
    const token = isolatedHarness.token;
    const api = {
      get: (p) => callApiAs(baseUrl, token, "GET", p),
      put: (p, b) => callApiAs(baseUrl, token, "PUT", p, b),
      post: (p, b) => callApiAs(baseUrl, token, "POST", p, b),
    };

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

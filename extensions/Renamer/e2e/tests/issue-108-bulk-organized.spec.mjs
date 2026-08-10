// Reproduction for issue #108: "Set a few files to organized, last one renamed only".
//
// The existing auto-rename-on-update coverage edits ONE video through its detail page. The reported
// failure is the bulk path — select N videos, set Organized=true once through the bulk edit dialog —
// which raises N separate video.updated events instead of one. Nothing covered that fan-out.
//
// Own harness per test, same reason as auto-rename-on-update.spec.mjs: AutoRenamerOnUpdate is a
// global extension setting that would leak into every other test sharing the worker's instance.
import { test as base, expect } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { seedVideo } from "@cove-extensions/e2e/seed-media";
import { RENAMER_EXTENSION } from "../lib/renamer-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.renamer";
const COUNT = 6; // the reporter's own number

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

// Mirrors the shared `api` fixture, which is bound to the shared harness and so cannot be used
// here. Always JSON.stringify: the extension's options endpoint takes the blob as a STRING, so a
// caller passes an already-stringified object and this encodes it a second time — deliberate, and
// the same shape cross-device-move.spec.mjs uses.
async function callApi(baseUrl, token, method, path, body) {
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

// KNOWN FAILING — issue #108, unfixed. Marked expected-to-fail so the suite stays green while the
// defect stays pinned executably rather than living only in an issue tracker. When the fix lands this
// test PASSES, which Playwright reports as an unexpected pass and fails the run — so the marker cannot
// outlive the bug. Remove `test.fail()` in the same change that fixes it.
//
// Reproduced 2026-08-09: options read back applied, the single-item control renamed, and the bulk path
// renamed 0 of 6. The control is what makes the 0 meaningful — without it, an unapplied setting and a
// broken hook are indistinguishable.
test.fail();
test("setting Organized on several videos at once renames every one of them, not just one", async ({
  isolatedHarness,
}) => {
  const baseUrl = isolatedHarness.baseUrl;
  const token = isolatedHarness.token;
  const api = {
    get: (p) => callApi(baseUrl, token, "GET", p),
    put: (p, b) => callApi(baseUrl, token, "PUT", p, b),
    post: (p, b) => callApi(baseUrl, token, "POST", p, b),
  };

  // The reporter's configuration: auto-rename on update, gated to organized items only.
  // "$title" alone makes each expected basename exact rather than merely "the path changed".
  const put = await api.put(
    `/api/extensions/${EXTENSION_ID}/data/options`,
    JSON.stringify({
      AutoRenamerOnUpdate: true,
      OnlyOrganized: true,
      FilenameTemplate: "$title",
    }),
  );
  expect(put.ok).toBe(true);

  // Read the options back. Without this a 0-renamed result is ambiguous — an unapplied setting and a
  // broken hook look identical, and the first would make the whole test prove nothing.
  // NOTE the route: the host exposes PUT `{id}/data/{key}` but only GET `{id}/data` (every key at
  // once). There is no per-key GET — asking for one returns the SPA shell, which parses to {} and
  // reads as "nothing persisted" no matter what was stored.
  const readBack = await api.get(`/api/extensions/${EXTENSION_ID}/data`);
  const rawOptions = readBack.json?.options;
  const applied = typeof rawOptions === "string" ? JSON.parse(rawOptions) : (rawOptions ?? {});
  console.log(`\nissue-108 options applied: ${JSON.stringify(applied)}\n`);
  expect(applied.AutoRenamerOnUpdate, "AutoRenamerOnUpdate did not persist").toBe(true);
  expect(applied.OnlyOrganized, "OnlyOrganized did not persist").toBe(true);

  // CONTROL: one video, organized individually through the per-entity route. If this does not
  // rename, the hook is off and the bulk result below says nothing about bulk.
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
  const controlNow = await api.get(`/api/videos/${control.id}`).then((r) => r.json);
  console.log(`\nissue-108 CONTROL renamed=${controlRenamed} path=${controlNow.files[0].path}\n`);
  expect(
    controlRenamed,
    "the single-item path did not rename either — the hook is off, so the bulk result proves nothing",
  ).toBe(true);

  // Seed COUNT videos and give each a distinct title while it is still UNorganized. Each title edit
  // raises its own video.updated, but the only-organized gate skips it — so nothing is renamed yet,
  // and the bulk organized flip below is the sole trigger under test.
  const videos = [];
  for (let i = 0; i < COUNT; i++) {
    const seeded = await seedVideo({ container: isolatedHarness.container, baseUrl });
    const title = `Bulk Organized Item ${i + 1}`;
    const upd = await api.put(`/api/videos/${seeded.id}`, { title });
    expect(upd.ok).toBe(true);
    videos.push({ id: seeded.id, title, originalPath: seeded.files[0].path });
  }

  // Nothing should have moved yet — proves the gate held and isolates the trigger.
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

  // Poll until every file has moved, or time out. Polling the whole set (rather than each in turn)
  // keeps a slow-but-correct fan-out from reading as a failure.
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

  const report = [];
  for (const v of videos) {
    const now = await api.get(`/api/videos/${v.id}`).then((r) => r.json);
    report.push(`  id=${v.id} organized=${now.organized} path=${now.files[0].path}`);
  }
  console.log(`\nissue-108: ${renamed.length}/${videos.length} renamed\n${report.join("\n")}\n`);

  // The bug: this is 1 (or some number < COUNT) instead of COUNT.
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

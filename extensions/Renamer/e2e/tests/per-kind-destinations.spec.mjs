// What the "Per kind" list does to files, driven through the real panel: a kind sent to a folder of
// its own lands there, an excluded kind is not touched at all, and a kind left alone follows the
// card's own destination. All three hold in one whole-library run, which is the combination that
// matters - a per-kind setting that leaked across kinds would still pass a one-kind test.
//
// The proof is exact on-disk + DB state for every kind, never the panel's own banner: an excluded
// kind's file must still be at the path it started from, which only a filesystem check can say.
import { test as base, expect, seedVideo, RENAMER_EXTENSION } from "../lib/renamer-fixtures.mjs";
import { seedImage, seedText } from "@cove-extensions/e2e/seed-media";
import { startHarness } from "@cove-extensions/e2e/harness";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

const test = base.extend({
  // "Rename all files" sweeps every item in the library, so this runs on its own instance - a sibling
  // test's seeded media sharing the per-worker harness would be swept into this run's scope.
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

function apiFor(baseUrl) {
  async function callApi(method, path, body) {
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
  return {
    get: (p) => callApi("GET", p),
    put: (p, b) => callApi("PUT", p, b),
  };
}

/** The path the DB currently holds for one item, whatever its kind. */
async function currentPath(api, route, id) {
  const res = await api.get(`/api/${route}/${id}`);
  expect(res.ok, `GET /api/${route}/${id} failed (${res.status}): ${res.text}`).toBe(true);
  return res.json.files[0].path;
}

/**
 * Asserts one item landed at exactly `expectedPath`: the DB record says so, the file is on disk
 * there, and the path it came from is gone. Polls the record, so the run's read-after-write window
 * is honored.
 */
async function assertLandedAt({ api, container, route, id, expectedPath, originalPath }) {
  const record = await pollUntil(
    () => api.get(`/api/${route}/${id}`).then((r) => r.json),
    (item) => item.files[0].path === expectedPath,
    { label: `${route} ${id} to land at exactly "${expectedPath}"` },
  );
  expect(
    record.files[0].path,
    `DB record for ${route} ${id} should point at "${expectedPath}"`,
  ).toBe(expectedPath);

  const onDisk = await container.exec(["test", "-f", expectedPath]);
  expect(onDisk.exitCode, `Renamed file "${expectedPath}" is missing from disk`).toBe(0);

  if (originalPath !== expectedPath) {
    const oldOnDisk = await container.exec(["test", "-f", originalPath]);
    expect(
      oldOnDisk.exitCode,
      `Original path "${originalPath}" still exists on disk after the rename`,
    ).not.toBe(0);
  }
}

/**
 * Asserts one item was left where it was: the DB still points at its original path, and a file is
 * still there. It does not compare contents, because a rename this run refused to make is a move,
 * not a write. A settled run is what makes it meaningful, so callers assert the kinds that did move
 * first: this check would pass on a run that had not started.
 */
async function assertUntouched({ api, container, route, id, originalPath }) {
  const path = await currentPath(api, route, id);
  expect(path, `${route} ${id} should not have been renamed`).toBe(originalPath);

  const onDisk = await container.exec(["test", "-f", originalPath]);
  expect(onDisk.exitCode, `Excluded file "${originalPath}" is missing from disk`).toBe(0);
}

test("one run honors a kind's own folder, an excluded kind and a kind on the default at once", async ({
  page,
  isolatedHarness,
}) => {
  const baseUrl = isolatedHarness.baseUrl;
  const container = isolatedHarness.container;
  const api = apiFor(baseUrl);

  const [video, image, text] = await Promise.all([
    seedVideo({ container, baseUrl, destName: `kinds-a-${Date.now()}.mp4` }),
    seedImage({ container, baseUrl, destName: `kinds-b-${Date.now()}.png` }),
    seedText({ container, baseUrl, destName: `kinds-c-${Date.now()}.txt` }),
  ]);
  const original = {
    video: video.files[0].path,
    image: image.files[0].path,
    text: text.files[0].path,
  };

  // A "$title"-only filename and literal folder names (no tokens) make every landing path
  // deterministic. With no routing rules a move is source-confined - a file may only move within its
  // own source root - so a bare relative sub-folder under /data is a permitted target.
  const titles = { video: "Kind Video", image: "Kind Image", text: "Kind Text" };
  expect((await api.put(`/api/videos/${video.id}`, { Title: titles.video })).ok).toBe(true);
  expect((await api.put(`/api/images/${image.id}`, { Title: titles.image })).ok).toBe(true);
  expect((await api.put(`/api/texts/${text.id}`, { Title: titles.text })).ok).toBe(true);

  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();
  await settingsPage.setFilenameTemplate("$title");
  // The card's own destination, which the text document is left to follow.
  await settingsPage.setFolderTemplate("everything-else");
  await settingsPage.setKindFolder("Videos", "own-videos");
  await settingsPage.excludeKind("Images");
  await settingsPage.save();

  await settingsPage.renameAll();

  await assertLandedAt({
    api,
    container,
    route: "videos",
    id: video.id,
    expectedPath: `/data/own-videos/${titles.video}.mp4`,
    originalPath: original.video,
  });
  await assertLandedAt({
    api,
    container,
    route: "texts",
    id: text.id,
    expectedPath: `/data/everything-else/${titles.text}.txt`,
    originalPath: original.text,
  });
  // Asserted last, after two kinds are proven moved: by then the run has demonstrably done its work,
  // so an untouched image is a decision rather than a race.
  await assertUntouched({
    api,
    container,
    route: "images",
    id: image.id,
    originalPath: original.image,
  });
});

test("the buttons swap which kinds move on the next run", async ({ page, isolatedHarness }) => {
  const baseUrl = isolatedHarness.baseUrl;
  const container = isolatedHarness.container;
  const api = apiFor(baseUrl);

  const [video, image] = await Promise.all([
    seedVideo({ container, baseUrl, destName: `swap-a-${Date.now()}.mp4` }),
    seedImage({ container, baseUrl, destName: `swap-b-${Date.now()}.png` }),
  ]);
  const originalVideoPath = video.files[0].path;
  const originalImagePath = image.files[0].path;

  expect((await api.put(`/api/videos/${video.id}`, { Title: "Swap Video" })).ok).toBe(true);
  expect((await api.put(`/api/images/${image.id}`, { Title: "Swap Image" })).ok).toBe(true);

  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();
  await settingsPage.setFilenameTemplate("$title");
  await settingsPage.setFolderTemplate("");
  await settingsPage.excludeKind("Images");
  await settingsPage.save();
  await settingsPage.renameAll();

  const renamedVideoPath = `/data/Swap Video.mp4`;
  await assertLandedAt({
    api,
    container,
    route: "videos",
    id: video.id,
    expectedPath: renamedVideoPath,
    originalPath: originalVideoPath,
  });
  await assertUntouched({
    api,
    container,
    route: "images",
    id: image.id,
    originalPath: originalImagePath,
  });

  // Now the other way round: the video stops being renamed and the image starts, into a folder of
  // its own. The video's proof is that a second whole-library run leaves it exactly where run one
  // put it.
  await settingsPage.goto();
  await settingsPage.includeKind("Images");
  await settingsPage.setKindFolder("Images", "now-images");
  await settingsPage.excludeKind("Videos");
  await settingsPage.save();
  await settingsPage.renameAll();

  await assertLandedAt({
    api,
    container,
    route: "images",
    id: image.id,
    expectedPath: `/data/now-images/Swap Image.png`,
    originalPath: originalImagePath,
  });
  await assertUntouched({
    api,
    container,
    route: "videos",
    id: video.id,
    originalPath: renamedVideoPath,
  });
});

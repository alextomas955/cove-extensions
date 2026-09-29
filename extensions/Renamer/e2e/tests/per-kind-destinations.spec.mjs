// What the "Per kind" list does to files, driven through the real panel: a kind sent to a folder of
// its own lands there, an excluded kind is not touched at all, and a kind left alone follows the
// card's own destination. All three hold in one whole-library run, which is the combination that
// matters - a per-kind setting that leaked across kinds would still pass a one-kind test. A second run
// then swaps which kinds move, so the buttons are proven to change the next run and not only the first.
//
// The proof is exact on-disk + DB state for every kind, never the panel's own banner: an excluded
// kind's file must still be at the path it started from, which only a filesystem check can say.
import { isolatedHarnessFixtures } from "@cove-extensions/e2e";
import { seedImage, seedText } from "@cove-extensions/e2e/seed-media";
import {
  test as base,
  expect,
  seedVideo,
  clientFor,
  RENAMER_EXTENSION,
} from "../lib/renamer-fixtures.mjs";
import { assertLandedAt, fileExists } from "../lib/rename-assertions.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

// "Rename all files" sweeps every item in the library, so this runs on its own instance - a sibling
// test's seeded media sharing the per-worker harness would be swept into this run's scope.
const test = base.extend(isolatedHarnessFixtures(RENAMER_EXTENSION));

/**
 * Asserts one item was left where it was: the DB still points at `path`, and a file is still there.
 * It does not compare contents, because a rename this run refused to make is a move, not a write. A
 * settled run is what makes it meaningful, so callers assert the kinds that did move first: this
 * check would pass on a run that had not started.
 */
async function assertUntouched({ api, container, route, id, path }) {
  const res = await api.get(`/api/${route}/${id}`);
  expect(res.ok, `GET /api/${route}/${id} failed (${res.status}): ${res.text}`).toBe(true);
  expect(res.json.files[0].path, `${route} ${id} should not have been renamed`).toBe(path);
  expect(await fileExists(container, path), `Excluded file "${path}" is missing from disk`).toBe(
    true,
  );
}

test("one run honors a kind's own folder, an excluded kind and a kind on the default, and the buttons swap them for the next run", async ({
  page,
  isolatedHarness,
}) => {
  const baseUrl = isolatedHarness.baseUrl;
  const container = isolatedHarness.container;
  const api = clientFor(isolatedHarness);

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

  const videoPath = `/data/own-videos/${titles.video}.mp4`;
  const textPath = `/data/everything-else/${titles.text}.txt`;
  await assertLandedAt({
    api,
    container,
    route: "videos",
    id: video.id,
    expectedPath: videoPath,
    originalPath: original.video,
  });
  await assertLandedAt({
    api,
    container,
    route: "texts",
    id: text.id,
    expectedPath: textPath,
    originalPath: original.text,
  });
  // Asserted after two kinds are proven moved: by then the run has demonstrably done its work, so an
  // untouched image is a decision rather than a race.
  await assertUntouched({ api, container, route: "images", id: image.id, path: original.image });

  // The other way round: the video stops being renamed and the image starts, into a folder of its
  // own. The video's proof is that a second whole-library run leaves it exactly where run one put it.
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
    expectedPath: `/data/now-images/${titles.image}.png`,
    originalPath: original.image,
  });
  await assertUntouched({ api, container, route: "videos", id: video.id, path: videoPath });
});

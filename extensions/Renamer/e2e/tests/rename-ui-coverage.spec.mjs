// UI-driven coverage for two rename-affecting affordances the rest of the suite never clicks: the
// FOLDER template ("Where files go" — relocates files on rename), and the whole-library "Rename all
// files" button in the settings panel (elsewhere exercised only via the raw renamer-library API job,
// never the actual button). Both are driven through the real UI via the Page Object Model; the proof
// is exact on-disk + DB state (assertRenamedTo), never the panel's own success banner.
import { test as base, expect, seedVideo, RENAMER_EXTENSION } from '../lib/renamer-fixtures.mjs';
import { startHarness } from '@cove-extensions/e2e/harness';
import { VideosPage } from '../lib/pages/videos-page.mjs';
import { RenamerSettingsPage } from '../lib/pages/renamer-settings-page.mjs';
import { assertRenamedTo } from '../lib/rename-assertions.mjs';

const test = base.extend({
  // "Rename all files" sweeps EVERY item in the library, so the whole-library test below runs on its
  // own instance — a sibling test's seeded media sharing the per-worker harness would be swept into
  // this run's scope and could miss its own polling window (same rationale as library-jobs.spec.mjs).
  isolatedHarness: [
    async ({}, use) => {
      const isolatedHarness = await startHarness();
      isolatedHarness.owner = await isolatedHarness.bootstrapOwner();
      await isolatedHarness.installExtension(RENAMER_EXTENSION);
      await use(isolatedHarness);
      await isolatedHarness.stop();
    },
    { scope: 'test' },
  ],
});

function apiFor(baseUrl) {
  async function callApi(method, path, body) {
    const res = await fetch(`${baseUrl}${path}`, {
      method,
      headers: body ? { 'Content-Type': 'application/json' } : undefined,
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
    get: (p) => callApi('GET', p),
    put: (p, b) => callApi('PUT', p, b),
  };
}

test('setting a folder template through the UI relocates a renamed file to the exact folder on disk and in the DB', async ({
  page,
  harness,
  baseUrl,
  api,
}) => {
  const video = await seedVideo({ container: harness.container, baseUrl });
  const originalFilename = video.files[0].path.split('/').pop();
  const originalPath = video.files[0].path;

  // A literal "relocated" folder (no tokens) plus a "$title"-only filename makes the whole landing
  // path deterministic. With no routing rules and empty AllowedRoots the move is source-confined —
  // the file may only move WITHIN its own source root (/data) — so a bare relative sub-folder under
  // /data is a genuinely permitted target; a routed root outside /data would be rejected. The file
  // must therefore land at exactly /data/relocated/<title>.mp4.
  const title = 'Folder Move Test';
  const folder = 'relocated';
  const expectedBasename = `${title}.mp4`;
  const expectedFullPath = `/data/${folder}/${expectedBasename}`;

  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();
  await settingsPage.setFilenameTemplate('$title');
  await settingsPage.setFolderTemplate(folder);
  await settingsPage.save();

  const videosPage = new VideosPage(page, baseUrl);
  await videosPage.goto();
  // Select by filename BEFORE setting a Title: the card's accessible name follows the title once set.
  await videosPage.selectCard(originalFilename);

  const setTitle = await api.put(`/api/videos/${video.id}`, { Title: title });
  expect(setTitle.ok).toBe(true);

  await videosPage.renameSelected();

  const newPath = await assertRenamedTo({
    api,
    container: harness.container,
    videoId: video.id,
    expectedBasename,
    originalPath,
  });
  // assertRenamedTo proves the basename + that the DB path exists on disk and the old path is gone;
  // the folder-relocation claim needs the FULL path pinned, or a move to the wrong (but existing)
  // folder would slip through.
  expect(newPath, 'renamed file must land in the exact folder the template computed').toBe(expectedFullPath);

  // Reset templates so the folder move + "$title" naming do not leak into a sibling test sharing this
  // worker's Cove instance (mirrors core-paths.spec.mjs's cleanup).
  await settingsPage.goto();
  await settingsPage.setFilenameTemplate('{$date - }$title{ [$resolution]}');
  await settingsPage.setFolderTemplate('');
  await settingsPage.save();
});

test('clicking "Rename all files" in the panel renames every library item to its exact name on disk and in the DB', async ({
  page,
  isolatedHarness,
}) => {
  const baseUrl = isolatedHarness.baseUrl;
  const container = isolatedHarness.container;
  const api = apiFor(baseUrl);

  // Persist a "$title"-only template through the real settings UI (not the options API) so each item's
  // computed name is deterministic and the panel button — disabled while dirty — becomes clickable.
  const settingsPage = new RenamerSettingsPage(page, baseUrl);
  await settingsPage.goto();
  await settingsPage.setFilenameTemplate('$title');
  await settingsPage.save();

  const videos = await Promise.all([
    seedVideo({ container, baseUrl, destName: `panel-a-${Date.now()}.mp4` }),
    seedVideo({ container, baseUrl, destName: `panel-b-${Date.now()}.mp4` }),
    seedVideo({ container, baseUrl, destName: `panel-c-${Date.now()}.mp4` }),
  ]);
  const originalPaths = videos.map((v) => v.files[0].path);

  const titles = ['Panel Rename Alpha', 'Panel Rename Bravo', 'Panel Rename Charlie'];
  for (let i = 0; i < videos.length; i++) {
    const update = await api.put(`/api/videos/${videos[i].id}`, { Title: titles[i] });
    expect(update.ok).toBe(true);
  }

  // The whole-library rename is triggered by the actual panel button here — NOT a POST to
  // renamer-library. That is the gap this test closes: the button-driven counterpart to
  // library-jobs.spec.mjs's API-driven run.
  await settingsPage.renameAll();

  for (let i = 0; i < videos.length; i++) {
    await assertRenamedTo({
      api,
      container,
      videoId: videos[i].id,
      expectedBasename: `${titles[i]}.mp4`,
      originalPath: originalPaths[i],
    });
  }
});

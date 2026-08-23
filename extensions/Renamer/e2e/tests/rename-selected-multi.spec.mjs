// The browser half of a MULTI-select "Rename selected": a rename driven from the grid with more than
// one card selected must reach every selected item, not just the first.
//
// The API half already exists (PreviewEndpointTests.PreviewAsync_WithSeveralEntityIds_ReturnsAnItemForEveryOne),
// and its own note records that the browser side had a single selectCard call and no multi-select
// helper. This is that missing half.
//
// Two shape rules, both load-bearing rather than stylistic:
//
// - It runs on isolatedTest, so the grid holds only what this spec seeded. The worker-shared harness
//   accumulates videos from sibling specs, and a grid of unknown contents makes "the first three
//   cards" a different set on every run.
// - Every assertion is a COUNT, never a displayed filename. selectFirstCards is position-based
//   precisely because a name-scoped selection is ordering-dependent on a shared grid, so asserting on
//   names would hand that dependence straight back. Even the dialog assertion reads the count the
//   confirm text quotes, not the examples it lists.
import {
  isolatedTest as test,
  expect,
  seedVideo,
  createApiClient,
  pollUntil,
} from "../lib/renamer-fixtures.mjs";
import { RenamerVideosPage } from "../lib/pages/renamer-videos-page.mjs";

const RENAMER_ID = "com.alextomas955.renamer";

// Three, not two: two cannot distinguish "renamed every selected item" from "renamed the first and
// the last", and one cannot distinguish multi-select from single-select at all.
const SELECTED = 3;

// `@smoke` — the multi-select grid path, which the single-item rename test cannot cover. See
// core-paths.spec.mjs for what the tag selects and why it is an option rather than part of the title.
test(
  "a rename driven from the grid with several cards selected renames every one of them",
  { tag: "@smoke" },
  async ({ page, isolatedHarness }) => {
    const baseUrl = isolatedHarness.baseUrl;
    const container = isolatedHarness.container;
    const api = createApiClient(baseUrl, isolatedHarness.token);

    // A literal in the template guarantees every planned name differs from the current one whatever the
    // fixture's own metadata resolves to, so "did this item's path change" stays a question about the
    // batch's reach rather than about what the seeded media happens to carry.
    const options = await api.put(
      `/api/extensions/${RENAMER_ID}/data/options`,
      JSON.stringify({ FilenameTemplate: "$title [multi]" }),
    );
    expect(options.ok, `seeding the template returned ${options.status}: ${options.text}`).toBe(
      true,
    );

    const stamp = Date.now();
    const videos = [];
    for (let i = 0; i < SELECTED; i++) {
      videos.push(
        await seedVideo({
          container,
          baseUrl,
          token: isolatedHarness.token,
          destName: `multi-${stamp}-${i}.mp4`,
        }),
      );
    }
    const originalPathById = new Map(videos.map((v) => [v.id, v.files[0].path]));

    // A title each, because Cove leaves a scanned item's Title null on purpose and the template above
    // renders $title behind a required-field gate — so without one every card would be a gated skip and
    // the count below would read as a multi-select failure rather than as the missing metadata it is.
    for (const [i, video] of videos.entries()) {
      const update = await api.put(`/api/videos/${video.id}`, { Title: `Multi ${stamp} ${i}` });
      expect(update.ok, `setting the title returned ${update.status}: ${update.text}`).toBe(true);
    }

    const videosPage = new RenamerVideosPage(page, baseUrl);
    await videosPage.goto();

    // Wait for the grid to hold every seeded card BEFORE selecting. selectFirstCards clamps to what is
    // present when it counts, so without this the count assertion below would be racing the grid's
    // client-side fetch instead of testing multi-select.
    await expect(videosPage.selectItemButtons).toHaveCount(SELECTED, { timeout: 15_000 });

    const selected = await videosPage.selectFirstCards(SELECTED);
    // Asserted BEFORE the rename is triggered, and this is the assertion that makes the rest mean
    // anything: selectFirstCards clamps with Math.min(count, available), so a grid holding one card
    // would otherwise let this spec pass having proved nothing about multi-select.
    expect(
      selected,
      `asked for ${SELECTED} cards and the grid only had ${selected} to select — nothing below would be about multi-select`,
    ).toBe(SELECTED);

    const messages = await videosPage.renameSelected();

    // The count the confirm gate quotes, not the example names it lists beneath it. The header reads
    // "N selected items" only when every selected item will actually change, so this also rules out a
    // partially-skipped batch reading as a full one.
    expect(
      messages.join("\n"),
      `the confirm gate did not offer to rename all ${SELECTED} selected items`,
    ).toContain(`Rename ${SELECTED} selected items?`);

    // The outcome, per entity id: as many stored paths changed as cards were selected. Polled because a
    // settled job does not guarantee the next read reflects its write (see poll.mjs).
    const changedPaths = () =>
      Promise.all(
        videos.map(async (v) => {
          const record = await api.get(`/api/videos/${v.id}`);
          return record.json.files[0].path;
        }),
      ).then((paths) => paths.filter((path, i) => path !== originalPathById.get(videos[i].id)));

    const changed = await pollUntil(changedPaths, (paths) => paths.length === selected, {
      label: `all ${selected} selected videos to report a changed stored path`,
    });
    expect(changed.length, "fewer items were renamed than were selected").toBe(selected);

    // And the disk agrees, still counted rather than named: every new path is a real file, and no
    // original path survives. A DB-only rename and a copy that left the source behind both pass the
    // count above and fail here.
    const onDisk = await Promise.all(changed.map((path) => container.exec(["test", "-f", path])));
    expect(
      onDisk.filter((probe) => probe.exitCode === 0).length,
      "a stored path has no file behind it",
    ).toBe(selected);

    const leftBehind = await Promise.all(
      [...originalPathById.values()].map((path) => container.exec(["test", "-f", path])),
    );
    expect(
      leftBehind.filter((probe) => probe.exitCode === 0).length,
      "an original file is still on disk after the rename",
    ).toBe(0);
  },
);

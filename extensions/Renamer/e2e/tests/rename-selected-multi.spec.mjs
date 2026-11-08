// A rename driven from the grid with more than one card selected must reach every selected item.
//
// Two shape rules:
//
// - It runs on its own instance, so the grid holds only what this spec seeded. The worker-shared
//   harness accumulates videos from sibling specs, and a grid of unknown contents makes "the first
//   three cards" a different set on every run.
// - Every assertion is a count, never a displayed filename. selectFirstCards is position-based
//   because a name-scoped selection is ordering-dependent on a shared grid, and asserting on names
//   would hand that dependence back.
// `@smoke` - part of the selection core-paths.spec.mjs explains.
import { isolatedHarnessFixtures } from "@cove-extensions/e2e";
import { VideosPage } from "@cove-extensions/e2e/pages/videos-page";
import {
  test as base,
  expect,
  seedVideo,
  pollUntil,
  clientFor,
  RENAMER_EXTENSION,
  EXTENSION_ID,
} from "../lib/renamer-fixtures.mjs";

// Three, not two: two cannot distinguish "renamed every selected item" from "renamed the first and
// the last", and one cannot distinguish multi-select from single-select at all.
const SELECTED = 3;

const test = base.extend(isolatedHarnessFixtures(RENAMER_EXTENSION));

test(
  "a rename driven from the grid with several cards selected renames every one of them",
  { tag: "@smoke" },
  async ({ page, isolatedHarness }) => {
    const baseUrl = isolatedHarness.baseUrl;
    const container = isolatedHarness.container;
    const api = clientFor(isolatedHarness);

    // A literal in the template makes every planned name differ from the current one whatever the
    // fixture's metadata resolves to, so "did this item's path change" stays a question about the
    // batch's reach.
    const options = await api.put(
      `/api/extensions/${EXTENSION_ID}/data/options`,
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

    // A title each, because Cove leaves a scanned item's Title null and the template above renders
    // $title behind a required-field gate. Without one every card is a gated skip, which reads as a
    // multi-select failure rather than as missing metadata.
    for (const [i, video] of videos.entries()) {
      const update = await api.put(`/api/videos/${video.id}`, { Title: `Multi ${stamp} ${i}` });
      expect(update.ok, `setting the title returned ${update.status}: ${update.text}`).toBe(true);
    }

    const videosPage = new VideosPage(page, baseUrl);
    await videosPage.goto();

    // selectFirstCards clamps to what is present when it counts, so without this the count below
    // races the grid's client-side fetch instead of testing multi-select.
    await expect(videosPage.selectItemButtons).toHaveCount(SELECTED, { timeout: 15_000 });

    const selected = await videosPage.selectFirstCards(SELECTED);
    // The clamp means a grid holding one card would otherwise let this spec pass having proved
    // nothing about multi-select.
    expect(
      selected,
      `asked for ${SELECTED} cards and the grid only had ${selected} to select - nothing below would be about multi-select`,
    ).toBe(SELECTED);

    const messages = await videosPage.renameSelected();

    // The count the confirm gate quotes, not the example names it lists beneath it. The header
    // reads "N selected items" only when every selected item will change, so this also rules out a
    // partially-skipped batch reading as a full one.
    expect(
      messages.join("\n"),
      `the confirm gate did not offer to rename all ${SELECTED} selected items`,
    ).toContain(`Rename ${SELECTED} selected items?`);

    // Polled because a settled job does not guarantee the next read reflects its write.
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

    // A DB-only rename and a copy that left the source behind both pass the count above and fail
    // here.
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

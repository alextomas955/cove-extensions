// Docs feature-walkthrough capture (env-gated, never part of the default CI suite). With WHISPARR_SHOTS=1
// it seeds the synthetic Phase-11 corpus into the harness Cove, drives each Whisparr Sync surface through
// the existing page objects, and overwrites the matching placeholder under website/static/img/whisparr-sync/
// with a real PNG. Every image is therefore rendered from the synthetic corpus only — no real media.
//
// It is best-effort per slot: a surface that cannot render (e.g. the studio monitor line needs a live
// Whisparr) is logged and left with its committed placeholder rather than failing the run. The default
// suite sets neither WHISPARR_SHOTS nor WHISPARR_E2E, so this whole file skips there and CI stays green.
import { mkdirSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { test, expect, seedCorpus } from "../lib/whisparrsync-fixtures.mjs";
import { SCREENSHOT_TARGETS, IMG_OUTPUT_SUBPATH } from "../lib/screenshot-targets.mjs";
import { WhisparrSettingsPage } from "../lib/pages/settings-page.mjs";
import { ScenePanel } from "../lib/pages/scene-panel.mjs";
import { VideosListPage } from "../lib/pages/videos-list-page.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

const HERE = dirname(fileURLToPath(import.meta.url)); // …/extensions/WhisparrSync/e2e/tests
const REPO_ROOT = join(HERE, "..", "..", "..", ".."); // repo root
const OUT_DIR = join(REPO_ROOT, IMG_OUTPUT_SUBPATH);

// One handler per slot `surface`. Each navigates via the existing page objects and returns the element to
// shoot, or `null` to shoot the whole page. A throw is caught by the caller so the slot keeps its placeholder.
const HANDLERS = {
  async "settings-connection"({ settings }) {
    await settings.goto();
    // An obviously-fake demo value — never a real key. The capture shows the form, not a live result.
    await settings.setConnection("http://localhost:6969", "synthetic-demo-api-key");
    return null;
  },
  async reconciliation({ page, settings }) {
    await settings.goto();
    await settings.reconciliationHeading.first().scrollIntoViewIfNeeded();
    await page.waitForTimeout(250);
    return null;
  },
  async "scene-panel"({ scene, seeded }) {
    const [first] = seeded.values();
    await scene.gotoVideo(first.coveVideoId);
    await scene.openWhisparrTab();
    return null;
  },
  async "videos-batch"({ videos }) {
    await videos.goto();
    await videos.selectFirstCards(2);
    await videos.openWhisparrBatchMenu();
    return null;
  },
  async "monitor-studio"({ entity, seeded }) {
    const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
    if (studioId == null) {
      throw new Error("no synthetic studio was linked in the seeded corpus");
    }
    await entity.gotoStudio(studioId);
    return null;
  },
  async "library-status"({ videos }) {
    await videos.goto();
    await videos.libraryToggle().click();
    return null;
  },
};

test.describe("docs screenshots", () => {
  test.skip(
    !process.env.WHISPARR_SHOTS,
    "docs screenshot capture — run locally with WHISPARR_SHOTS=1 (see e2e/README.md)",
  );

  test("capture the walkthrough screenshots from the synthetic corpus", async ({
    harness,
    baseUrl,
    page,
  }) => {
    mkdirSync(OUT_DIR, { recursive: true });
    const seeded = await seedCorpus({ container: harness.container, baseUrl });

    const ctx = {
      page,
      baseUrl,
      seeded,
      settings: new WhisparrSettingsPage(page, baseUrl),
      scene: new ScenePanel(page, baseUrl),
      videos: new VideosListPage(page, baseUrl),
      entity: new EntityDetailPage(page, baseUrl),
    };

    const captured = [];
    for (const target of SCREENSHOT_TARGETS) {
      const handler = HANDLERS[target.surface];
      expect(handler, `no capture handler for slot "${target.surface}"`).toBeDefined();
      try {
        const element = await handler(ctx);
        await (element ?? page).screenshot({ path: join(OUT_DIR, target.file) });
        captured.push(target.file);
      } catch (err) {
        // A surface the current harness cannot render keeps its committed placeholder — never a failure.
        console.warn(`[screenshots] skipped ${target.file}: ${err.message}`);
      }
    }

    console.log(`[screenshots] captured ${String(captured.length)}/${String(SCREENSHOT_TARGETS.length)}: ${captured.join(", ")}`);
    expect(captured.length, "no surface could be captured — is the harness up?").toBeGreaterThan(0);
  });
});

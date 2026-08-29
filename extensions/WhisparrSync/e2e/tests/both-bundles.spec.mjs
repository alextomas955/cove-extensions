// Two extensions installed into ONE Cove, and what the newer bundle does to the older one's UI.
//
// The host loads every extension's bundle under a single promise, so a bundle that throws takes down
// every other extension's UI on the page with it. The blast radius therefore runs from the new
// extension to the established one, which is why Renamer's surface is asserted FIRST here: a change
// touching only Whisparr Sync can blank a panel that belongs to Renamer, and nothing else in this
// repo would say so.
//
// This spec is run by hand. It is skipped unless {@link RUN_FLAG} is set, so no automated job
// collects it and no merge gate can come to depend on it.
//
// Accepted debt: the Renamer imports below reach sideways into a sibling extension's e2e library,
// which the repo's depend-downward rule otherwise discourages. Observing both extensions on one page
// is the whole subject, and no downward-only import can express it.
import { test as base, expect } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { RENAMER_EXTENSION } from "../../../Renamer/e2e/lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../../../Renamer/e2e/lib/pages/renamer-settings-page.mjs";
import { WHISPARR_SYNC_EXTENSION } from "../lib/whisparr-sync-fixtures.mjs";

const RUN_FLAG = "COVE_E2E_BOTH_BUNDLES";

const WHISPARR_SYNC_SETTINGS_PATH = "/settings/whisparr-sync";

// The sentence lives only inside the component this extension ships. The host draws the tab button,
// the heading and the manifest description from the manifest alone, so an assertion on the
// extension's NAME passes just as happily against a bundle that never loaded.
const WHISPARR_SYNC_STUB_SENTENCE =
  "Connection setup for Whisparr Sync arrives in a later release.";

const ATTEMPT_BUDGET_MS = 60_000;
const ATTEMPTS = 3;

const bothBundles = base.extend({
  // Its own instance, not the worker-shared one: this installs two extensions and restarts the
  // container twice, and either would leak into every sibling test sharing a worker's Cove.
  bothInstalled: [
    async ({}, use) => {
      const harness = await startHarness();
      try {
        harness.owner = await harness.bootstrapOwner();
        // The helper takes one descriptor, so two extensions means two calls. Each copies into its own
        // `/config/extensions/<id>` and restarts, and the restart is what makes the host discover it.
        const renamer = await harness.installExtension(RENAMER_EXTENSION);
        const whisparrSync = await harness.installExtension(WHISPARR_SYNC_EXTENSION);
        await use({ harness, renamerId: renamer.id, whisparrSyncId: whisparrSync.id });
      } finally {
        await harness.stop();
      }
    },
    { scope: "test" },
  ],

  // Read through the handle AFTER both installs. Each restart can republish the container on a
  // different host port, so a URL captured before them addresses nothing.
  baseUrl: async ({ bothInstalled }, use) => {
    await use(bothInstalled.harness.baseUrl);
  },
});

bothBundles.describe("both extension bundles in one Cove", () => {
  bothBundles.skip(
    !process.env[RUN_FLAG],
    `run by hand with ${RUN_FLAG} set; deliberately not a CI gate, so nothing automated depends on it`,
  );

  bothBundles(
    "the established extension's settings surface survives the newer extension's bundle",
    async ({ page, baseUrl, bothInstalled }) => {
      // The controls of BOTH installs, so a green run cannot mean the host quietly kept one.
      const listed = await fetch(`${baseUrl}/api/extensions`, {
        headers: bothInstalled.harness.token
          ? { Authorization: `Bearer ${bothInstalled.harness.token}` }
          : {},
      });
      expect(listed.status, `GET ${baseUrl}/api/extensions did not answer 200`).toBe(200);
      const enabled = new Set(
        (await listed.json()).filter((entry) => entry.enabled).map((entry) => entry.id),
      );
      expect(
        [...enabled],
        "the host does not report both extensions enabled, so nothing below measures two bundles",
      ).toEqual(expect.arrayContaining([bothInstalled.renamerId, bothInstalled.whisparrSyncId]));

      // FIRST, and this order is the point. Renamer's own settings content is the thing a broken
      // Whisparr Sync bundle would silently take with it.
      const renamerSettings = new RenamerSettingsPage(page, baseUrl);
      await renamerSettings.goto();
      await expect(
        renamerSettings.filenameTemplateInput,
        "Renamer's settings panel did not render alongside the second extension's bundle",
      ).toBeVisible();

      // Second: the new extension's own surface, so a pass cannot mean its bundle was simply absent.
      const stub = page.getByText(WHISPARR_SYNC_STUB_SENTENCE, { exact: true });
      const panelUrl = `${baseUrl}${WHISPARR_SYNC_SETTINGS_PATH}`;
      // The host carries an unknown settings key only until it finishes loading extensions, then
      // rewrites the address to its first built-in tab. Nothing after that rewrite can reach the
      // panel, and only a fresh navigation recovers it.
      for (let attempt = 1; attempt <= ATTEMPTS; attempt++) {
        await page.goto(panelUrl);
        const rendered = await stub
          .waitFor({ state: "visible", timeout: ATTEMPT_BUDGET_MS })
          .then(() => true)
          .catch(() => false);
        if (rendered) break;
      }
      await expect(
        stub,
        `Whisparr Sync's stub sentence never rendered at ${panelUrl} across ${ATTEMPTS} navigation(s); the page is now at ${page.url()}`,
      ).toBeVisible();
    },
  );
});

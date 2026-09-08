// The scene tab on a video detail page, in a real containerized host.
//
// WHY THIS SPEC EXISTS. Nothing below the browser can see the whole path this tab needs. Four
// strings bind it across two repositories: the manifest's page type, its tab key, its component
// name and the key the bundle registers a component under. The host resolves the last pair by exact
// string and renders nothing, with no error anywhere, when they differ. Behind the tab sit the read
// route, the identity resolution off the library's own stored row, the read against the instance
// and the projection, and a break in any one of them shows up as a tab that draws nothing.
//
// WHAT IT NEEDS. A Cove container, an installed extension and a real Whisparr instance. No metadata
// credential: the identifier the scene is named by is the library's own stored row, and this surface
// reaches no provider at all.
//
// THE ASSERTED STATE IS THE INSTANCE'S OWN. The seed answers with the entity as the instance
// projects it, and the expected chip label is derived from that answer. A label derived from what
// the seed asked for would agree with itself if the read or the projection dropped the value.
//
// WHY THE TITLE MATTERS. Playwright's --grep matches the concatenated title and never the filename,
// so the describe title below is what selects this file's tests. Every test added here goes inside
// the same block.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { registerRootFolder, startWhisparr } from "@cove-extensions/e2e/whisparr";
import { randomUUID } from "node:crypto";

import {
  test as base,
  connectWhisparr,
  expect,
  seedCoveVideo,
  STASHDB_ENDPOINT,
  WHISPARR_ROOT,
  WHISPARR_SYNC_EXTENSION,
} from "../lib/whisparr-sync-fixtures.mjs";

// The tab's label, transcribed by hand from the manifest that advertises it. A spec importing the
// same constant the manifest declares would be asserting that a string equals itself.
const TAB_LABEL = "Whisparr";

// The label of the tab's own state row, transcribed the same way.
const STATE_ROW_LABEL = "State";

// The two labels a held scene's state can read as, transcribed from the shipped vocabulary. The
// three remaining labels belong to states a scene the instance holds and reports on cannot be in.
const MONITORED = "Monitored";
const UNMONITORED = "Unmonitored";

// Each budget names the operation it bounds, so a failure says which one blew it rather than
// reporting the whole test as a timeout naming nothing.
const PAGE_BUDGET_MS = 60_000;
const PAGE_ATTEMPTS = 3;
const TAB_BUDGET_MS = 30_000;
const REGION_BUDGET_MS = 90_000;

const test = base.extend({
  sceneHarness: [
    async ({}, use) => {
      const harness = await startHarness();
      try {
        harness.owner = await harness.bootstrapOwner();
        await harness.installExtension(WHISPARR_SYNC_EXTENSION);
        await use(harness);
      } finally {
        await harness.stop();
      }
    },
    { scope: "test" },
  ],

  // Read through the handle AFTER the install. The install restarts the container, which re-mints
  // the token and can republish the instance on a different host port.
  baseUrl: async ({ sceneHarness }, use) => {
    await use(sceneHarness.baseUrl);
  },
});

/** The tab, by the only name the host draws it under. */
const whisparrTab = (page) => page.getByRole("tab", { name: TAB_LABEL, exact: true }).first();

/** The host's own detail-tab strip, which tells a page that has rendered from one still loading. */
const hostDetailTabs = (page) => page.getByRole("tablist").first();

/**
 * Opens `path`, re-navigating while nothing the caller named has rendered.
 *
 * The host paints its own error boundary in place of a page whose lazily-imported chunk failed to
 * fetch, on the correct URL and indefinitely. Only a fresh navigation recovers it, and the retry is
 * bounded so a permanent failure is not turned into a hung test.
 */
async function visit(page, baseUrl, path, present, label) {
  for (let attempt = 1; attempt <= PAGE_ATTEMPTS; attempt++) {
    await page.goto(`${baseUrl}${path}`);
    const rendered = await present
      .waitFor({ state: "visible", timeout: PAGE_BUDGET_MS })
      .then(() => true)
      .catch(() => false);
    if (rendered) return;
  }
  throw new Error(
    `${label}: nothing rendered at ${baseUrl}${path} across ${PAGE_ATTEMPTS} navigation(s) of ${PAGE_BUDGET_MS}ms each; the page is now at ${page.url()}`,
  );
}

/**
 * Seeds one scene on both sides and answers with the Cove video and the state the INSTANCE reports.
 *
 * The instance's entry is written into its own datastore rather than added through its API: an add
 * resolves the identifier against the vendor's metadata service, so a scene's mere existence would
 * depend on someone else's uptime. The seed reads the row back through the instance's own API, and
 * the expected label is derived from that read.
 */
async function seedScene(coveApi, whisparr, { label, monitored }) {
  const remoteId = randomUUID();

  const onInstance = await whisparr.seedEntity("v3", {
    kind: "scene",
    foreignId: remoteId,
    title: `Whisparr ${label}`,
    monitored,
  });

  const title = `${label} ${remoteId.slice(0, 8)}`;
  const video = await seedCoveVideo(coveApi, {
    title,
    remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId }],
  });

  expect(
    typeof onInstance.monitored,
    `the instance projected the seeded scene "${remoteId}" without a monitored flag, so there is no state to compare the tab against`,
  ).toBe("boolean");

  return { ...video, title, remoteId, statedAs: onInstance.monitored ? MONITORED : UNMONITORED };
}

test.describe("scene tab", () => {
  test("states the state Whisparr holds for the scene", async ({ page, baseUrl, sceneHarness }) => {
    // A container pair, an extension install, a browser and a real instance. Well above the shared
    // per-test budget, and deliberately its own number rather than a raised default for every spec.
    test.setTimeout(900_000);

    const coveApi = createApiClient(
      () => sceneHarness.baseUrl,
      () => sceneHarness.token,
    );

    // Everything the browser reported, so a bundle-load throw is named by this spec rather than
    // left as a blank region someone has to go and explain.
    const consoleErrors = [];
    page.on("console", (message) => {
      if (message.type() === "error") consoleErrors.push(message.text());
    });
    page.on("pageerror", (failure) => {
      consoleErrors.push(String(failure));
    });

    const whisparr = await startWhisparr({
      network: sceneHarness.container.getNetworkNames()[0],
      generations: ["v3"],
    });

    try {
      whisparr.v3.rootFolder = await registerRootFolder(
        whisparr.v3.container,
        whisparr.apiFor("v3"),
        "v3",
        WHISPARR_ROOT,
      );
      await connectWhisparr(coveApi, whisparr, "v3");

      const scene = await seedScene(coveApi, whisparr, { label: "Held", monitored: true });

      await visit(
        page,
        baseUrl,
        `/video/${String(scene.id)}`,
        hostDetailTabs(page),
        "the video detail page",
      );

      // The host wires its own tab list per page type and adds the extension tabs the manifest
      // declares, so an absent tab here is this extension's registration rather than the host's
      // reach.
      await expect(
        whisparrTab(page),
        `the video detail page: the host drew its own detail tabs and no ${TAB_LABEL} tab, so this extension's tab registration did not reach the manifest the host served.`,
      ).toBeVisible({ timeout: TAB_BUDGET_MS });

      await whisparrTab(page).click();

      await expect(
        page.getByText(STATE_ROW_LABEL, { exact: true }),
        "the tab mounted and drew no state row. A blank region is what a wrong component-map key looks like: it resolves to nothing, renders nothing and reports nothing.",
      ).toBeVisible({ timeout: REGION_BUDGET_MS });

      await expect(
        page.getByText(scene.statedAs, { exact: true }),
        `the tab drew no state chip reading "${scene.statedAs}", which is what the instance itself answered for the seeded scene`,
      ).toBeVisible({ timeout: REGION_BUDGET_MS });

      // Does NOT depend on the tab rendering. A wrong export name throws an ESM SyntaxError at
      // bundle load, and the host loads every extension bundle under one promise, so that one throw
      // takes down every extension surface on the page with no build failure anywhere.
      const loadFailures = consoleErrors.filter((line) =>
        /SyntaxError|component not found|does not provide an export/i.test(line),
      );
      expect(
        loadFailures,
        `the browser reported a bundle-load failure: ${loadFailures.join(" | ")}`,
      ).toEqual([]);
    } finally {
      await whisparr.stop();
    }
  });
});

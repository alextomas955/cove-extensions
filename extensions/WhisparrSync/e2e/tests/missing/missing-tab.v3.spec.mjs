// The catalogue tab in a real containerized host, and the proof that this bundle still loads.
//
// WHY THIS SPEC EXISTS. The host component declarations this surface imports are a hand
// transcription of a module in Cove's own checkout, and nothing in this repository can check one. A
// wrong prop shape type-checks. A wrong EXPORT NAME throws an ESM SyntaxError at bundle load, and
// the host loads every extension bundle under one promise, so that one throw takes down every
// extension surface on the page with no build failure anywhere. This spec loads a built bundle in a
// real host, which is the only evidence that the transcription is right. The bundle imports the tab
// component, which imports the module re-exporting all seven symbols, so all seven are resolved when
// the bundle is evaluated whether or not the tab is ever drawn.
//
// THE ORDER IS THE POINT. The settings panel is asserted FIRST and again LAST. First, because a
// bundle that failed to load makes nothing below meaningful. Last, because everything between the
// two navigations exercises the bundle further.
//
// WHAT SKIPS, AND WHY. One assertion is conditional and names its reason in an annotation rather
// than passing silently:
//
// - The STATUS-PILL assertion needs a real metadata credential, lifted read-only from this machine's
//   own Cove install. A machine with none is the ordinary case off this desk.
//
// The bundle-load assertions never skip, because they are the ones this spec exists for.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { registerRootFolder, startWhisparr } from "@cove-extensions/e2e/whisparr";
import { randomUUID } from "node:crypto";

import { configureProviderStub, startProviderStub } from "../../lib/provider-stub.mjs";
import {
  test as base,
  connectWhisparr,
  expect,
  seedCovePerformer,
  seedCoveStudio,
  STASHDB_ENDPOINT,
  WHISPARR_ROOT,
  WHISPARR_SYNC_EXTENSION,
} from "../../lib/whisparr-sync-fixtures.mjs";

// The sentence the settings panel itself draws. It exists only inside the component this extension
// ships, so reaching it means the whole bundle loaded and the host resolved its component map.
const PANEL_SENTENCE =
  "The address Cove itself reaches Whisparr on, including the scheme and port.";
const SETTINGS_PATH = "/settings/whisparr-sync";

// The tab's label, transcribed by hand from the manifest that advertises it. A spec importing the
// same constant the manifest declares would be asserting that a string equals itself.
const TAB_LABEL = "Missing";

// The four words a status pill can carry, transcribed from the shipped vocabulary the same way.
const PILL_WORDS = ["Wanted", "Unmonitored", "Not added", "Status unknown"];

// The studio this spec reads a catalogue for. A real StashDB studio with a real catalogue, so a page
// of cards is reachable when a credential is available; the uuid is what Cove stores as its remote
// id and what the extension subtracts ownership on.
const BRAZZERS_EXXTRA = "39cee498-a9ac-4403-910a-1a0157ad22d8";

// Each budget names the operation it bounds, so a failure says which one blew it rather than
// reporting the whole test as a timeout naming nothing.
const BUNDLE_BUDGET_MS = 60_000;
const BUNDLE_ATTEMPTS = 3;
// The tab is served in the manifest the host already loaded, and it is drawn with the tab strip this
// budget starts counting from, so it arrives in well under this on a cold container.
const TAB_BUDGET_MS = 30_000;
const REGION_BUDGET_MS = 90_000;

const test = base.extend({
  missingHarness: [
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
  baseUrl: async ({ missingHarness }, use) => {
    await use(missingHarness.baseUrl);
  },
});

/** The tab, by the only name the host draws it under. */
const missingTab = (page) => page.getByRole("tab", { name: TAB_LABEL }).first();

/** The host's own detail-tab strip, which tells a page that has rendered from one still loading. */
const hostDetailTabs = (page) => page.getByRole("tablist").first();

/** The cards the grid drew. */
const cards = (page) => page.locator("article").filter({ has: page.locator("img, h3") });

/** Any sentence the tab stated in place of a grid. */
const statedReasons = (page) => page.locator("p").filter({ hasText: /\S/ });

/**
 * Opens `path`, re-navigating while nothing the caller named has rendered.
 *
 * The host carries an unknown settings key only until it finishes loading extensions, then rewrites
 * the address to its first built-in tab; and it paints its own error boundary in place of a page
 * whose lazily-imported chunk failed to fetch, on the correct URL and indefinitely. Only a fresh
 * navigation recovers either, and the retry is bounded so a permanent failure is not turned into a
 * hung test.
 */
async function visit(page, baseUrl, path, present, label) {
  for (let attempt = 1; attempt <= BUNDLE_ATTEMPTS; attempt++) {
    await page.goto(`${baseUrl}${path}`);
    const rendered = await present
      .waitFor({ state: "visible", timeout: BUNDLE_BUDGET_MS })
      .then(() => true)
      .catch(() => false);
    if (rendered) return;
  }
  throw new Error(
    `${label}: nothing rendered at ${baseUrl}${path} across ${BUNDLE_ATTEMPTS} navigation(s) of ${BUNDLE_BUDGET_MS}ms each; the page is now at ${page.url()}`,
  );
}

test("the bundle loads with the tab in it, and the tab renders on every page it registers for", async ({
  page,
  baseUrl,
  missingHarness,
}) => {
  // Two container pairs, an extension install, a browser and a real instance. Well above the shared
  // per-test budget, and deliberately its own number rather than a raised default for every spec.
  test.setTimeout(900_000);

  const coveApi = createApiClient(
    () => missingHarness.baseUrl,
    () => missingHarness.token,
  );

  // Everything the browser reported, so a bundle-load throw is named by this spec rather than left
  // as a blank region someone has to go and explain.
  const consoleErrors = [];
  page.on("console", (message) => {
    if (message.type() === "error") consoleErrors.push(message.text());
  });
  page.on("pageerror", (failure) => {
    consoleErrors.push(String(failure));
  });

  const whisparr = await startWhisparr({
    network: missingHarness.container.getNetworkNames()[0],
    generations: ["v3"],
  });
  const provider = await startProviderStub({
    networkName: missingHarness.container.getNetworkNames()[0],
  });

  try {
    const instance = whisparr.apiFor("v3");
    whisparr.v3.rootFolder = await registerRootFolder(
      whisparr.v3.container,
      instance,
      "v3",
      WHISPARR_ROOT,
    );
    await connectWhisparr(coveApi, whisparr, "v3");

    // The catalogue is served on this network under the metadata service's own name, so the
    // assertions over it run wherever this suite runs.
    await configureProviderStub(coveApi);

    const studio = await seedCoveStudio(coveApi, {
      name: `Brazzers Exxtra ${randomUUID().slice(0, 8)}`,
      remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: BRAZZERS_EXXTRA }],
    });
    const performer = await seedCovePerformer(coveApi, {
      name: `Performer ${randomUUID().slice(0, 8)}`,
      remoteIds: [],
    });
    const tag = await coveApi.post("/api/tags", { name: `Tag ${randomUUID().slice(0, 8)}` });
    expect(tag.status, `POST /api/tags answered ${String(tag.status)}`).toBeLessThan(300);

    // FIRST, and the order is the point. A bundle that throws takes down every extension surface on
    // the page, so a failure here means nothing below is meaningful.
    await visit(
      page,
      baseUrl,
      SETTINGS_PATH,
      page.getByText(PANEL_SENTENCE, { exact: true }),
      "the whole-bundle load",
    );

    // The host wires its own tab list per entity page and adds the extension tabs the manifest
    // declares, so an absent tab here is this extension's registration rather than the host's reach.
    await visit(
      page,
      baseUrl,
      `/studio/${String(studio.id)}`,
      hostDetailTabs(page),
      "the studio detail page",
    );
    await expect(
      missingTab(page),
      `the studio detail page: the host drew its own detail tabs and no ${TAB_LABEL} tab, so this extension's tab registration did not reach the manifest the host served.`,
    ).toBeVisible({ timeout: TAB_BUDGET_MS });

    // Each page type is its own registration, and one component serves all three by reading its kind
    // from the address.
    for (const [path, where] of [
      [`/performer/${String(performer.id)}`, "the performer detail page"],
      [`/tag/${String(tag.json.id)}`, "the tag detail page"],
    ]) {
      await visit(page, baseUrl, path, hostDetailTabs(page), where);
      await expect(
        missingTab(page),
        `${where}: the host drew a ${TAB_LABEL} tab on the studio page and none here, so this page type's registration does not resolve.`,
      ).toBeVisible({ timeout: TAB_BUDGET_MS });
    }

    // Mounting the tab is what renders the transcribed host components, so a wrong PROP SHAPE shows
    // up here as a region that draws nothing.
    await visit(
      page,
      baseUrl,
      `/studio/${String(studio.id)}`,
      missingTab(page),
      "the studio detail page",
    );
    await missingTab(page).click();

    // Either cards or a stated reason, and never a blank region: a tab that mounted and drew nothing
    // is the failure a reader cannot tell from a catalogue that is genuinely empty.
    await expect
      .poll(async () => (await cards(page).count()) + (await statedReasons(page).count()), {
        timeout: REGION_BUDGET_MS,
        message:
          "the tab mounted and rendered neither a card nor a sentence. A blank region is what a wrong host-component prop shape looks like: it type-checks, renders nothing and reports nothing.",
      })
      .toBeGreaterThan(0);

    // The transcription proof, and it does NOT depend on the tab rendering. The bundle imports the
    // tab component, which imports the module re-exporting all seven host symbols, so every one of
    // those names is resolved when the bundle is evaluated. A wrong export name throws an ESM
    // SyntaxError there and takes the settings panel down with it.
    const transcriptionFailures = consoleErrors.filter((line) =>
      /SyntaxError|component not found|does not provide an export/i.test(line),
    );
    expect(
      transcriptionFailures,
      `the browser reported a bundle-load failure, which is what a wrong host-symbol transcription produces: ${transcriptionFailures.join(" | ")}`,
    ).toEqual([]);

    const first = cards(page).first();
    await expect(
      first,
      "a catalogue was served, so the tab should have answered with cards",
    ).toBeVisible({ timeout: REGION_BUDGET_MS });

    // Read off the stub's own record: the cards are evidence about this product only if the page
    // they came from is the one this spec served.
    expect(
      (await provider.asked()).filter((line) => line.includes("MissingPage")),
      "the stub was never asked for a page, so the tab is drawing something this spec did not serve",
    ).not.toEqual([]);
    await expect(
      first.getByText(new RegExp(PILL_WORDS.join("|"))),
      "the first card carries no status pill in this product's own vocabulary",
    ).toBeVisible();

    // LAST, and for the reason stated at the head of this file: the tab is what pulls the host
    // component module in, so the bundle has to still be intact after it mounted.
    await visit(
      page,
      baseUrl,
      SETTINGS_PATH,
      page.getByText(PANEL_SENTENCE, { exact: true }),
      "the bundle after the tab mounted",
    );
  } finally {
    await Promise.allSettled([whisparr.stop(), provider.stop()]);
  }
});

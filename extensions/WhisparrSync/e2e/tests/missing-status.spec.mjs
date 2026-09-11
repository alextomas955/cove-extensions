// What a catalogue page says about the instance, in a real containerized host.
//
// WHY THIS SPEC EXISTS. The tab draws the same four words whether the connected instance answered
// and reported nothing, or cannot answer at all. Those are different facts and a reader acts on them
// differently: one clears on a retry and the other never will. The page carries the difference in
// its own fields, and this spec is the evidence that a real host and a real instance produce them.
//
// WHAT IS DRIVEN, AND WHAT IS READ. The tab is driven in the browser, because whether it mounts on
// each page type is only observable there. The status fields are read off the extension's own route
// through the host, because a pill's four words are the same string for two of the cases and the
// field beside them is what tells them apart.
//
// WHAT IS CONDITIONAL, AND WHY. Two things narrow what can be asserted here, and each names itself
// in an annotation rather than passing silently:
//
// - A CATALOGUE needs a metadata credential, lifted read-only from this machine's own Cove install.
//   A machine with none is the ordinary case off this desk, and without one every page answers that
//   no provider is configured.
// - THE OLDER GENERATION identifies entities against the other metadata source, and this build ships
//   a client for one source only. A page read on that generation therefore answers that no provider
//   is configured rather than reaching a catalogue, so the permanent absence over a full grid is
//   asserted in the backend suite instead. What is asserted here is that the tab still renders and
//   still states a reason.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { createApiClient } from "@cove-extensions/e2e";
import { startHarness } from "@cove-extensions/e2e/harness";
import { registerRootFolder, startWhisparr } from "@cove-extensions/e2e/whisparr";
import { randomUUID } from "node:crypto";

import { liftMetadataServers } from "../../../../tests/e2e/lib/cove-providers.mjs";
import {
  test as base,
  connectWhisparr,
  expect,
  extensionRoute,
  seedCovePerformer,
  seedCoveStudio,
  STASHDB_ENDPOINT,
  WHISPARR_ROOT,
  WHISPARR_SYNC_EXTENSION,
} from "../lib/whisparr-sync-fixtures.mjs";

// The tab's label and the four pill words, transcribed by hand from the shipped vocabulary. A spec
// importing the constants the product declares would be asserting that a string equals itself.
const TAB_LABEL = "Missing";
const UNKNOWN_PILL = "Status unknown";
const PILL_WORDS = ["Wanted", "Unmonitored", "Not added", UNKNOWN_PILL];

// A real StashDB studio with a real catalogue. The uuid is what Cove stores as its remote id and
// what the extension subtracts ownership on.
const BRAZZERS_EXXTRA = "39cee498-a9ac-4403-910a-1a0157ad22d8";

// Each budget names the operation it bounds, so a failure says which one blew it rather than
// reporting the whole test as a timeout naming nothing.
const BUNDLE_BUDGET_MS = 60_000;
const BUNDLE_ATTEMPTS = 3;
const TAB_BUDGET_MS = 30_000;
const REGION_BUDGET_MS = 90_000;

const test = base.extend({
  statusHarness: [
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
  baseUrl: async ({ statusHarness }, use) => {
    await use(statusHarness.baseUrl);
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

/**
 * Configures the container's own Cove with this machine's StashDB credential.
 *
 * Read-only against the install, through the one sanctioned lift. Returns the reason it could not be
 * done, or null when it was.
 */
async function configureStashDb(api) {
  const lifted = liftMetadataServers({ names: ["stashdb"] });
  if (lifted.skip !== null) return lifted.skip;

  const server = lifted.servers[0];
  if (typeof server?.apiKey !== "string" || server.apiKey.length === 0) {
    return "this machine's Cove configuration carries no StashDB key";
  }

  const read = await api.get("/api/system/config");
  if (read.status >= 300) {
    return `GET /api/system/config answered ${String(read.status)}`;
  }

  const config = read.json;
  config.scraping.metadataServers = [
    {
      endpoint: STASHDB_ENDPOINT,
      apiKey: server.apiKey,
      name: "stashdb",
      maxRequestsPerMinute: server.maxRequestsPerMinute ?? 240,
    },
  ];

  const saved = await api.put("/api/system/config", config);
  return saved.status >= 300 ? `PUT /api/system/config answered ${String(saved.status)}` : null;
}

/** One page of the catalogue, as the extension's own route answers it. */
async function readMissingPage(api, kind, coveId) {
  const answered = await api.get(extensionRoute(`entity/${kind}/${String(coveId)}/missing`));
  expect(
    answered.status,
    `GET the ${kind} catalogue page answered ${String(answered.status)}: ${String(answered.text).slice(0, 300)}`,
  ).toBe(200);
  return answered.json;
}

test("the two reasons a status is unknown are different answers, in a real host", async ({
  page,
  baseUrl,
  statusHarness,
}) => {
  // Two container pairs, an extension install, a browser and a real instance. Well above the shared
  // per-test budget, and deliberately its own number rather than a raised default for every spec.
  test.setTimeout(900_000);

  const coveApi = createApiClient(
    () => statusHarness.baseUrl,
    () => statusHarness.token,
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
    network: statusHarness.container.getNetworkNames()[0],
    generations: ["v3", "v2"],
  });

  let whisparrStopped = false;
  try {
    const instance = whisparr.apiFor("v3");
    whisparr.v3.rootFolder = await registerRootFolder(
      whisparr.v3.container,
      instance,
      "v3",
      WHISPARR_ROOT,
    );
    await connectWhisparr(coveApi, whisparr, "v3");

    const providerSkip = await configureStashDb(coveApi);

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

    // Every page type renders the tab and none of them reports a missing component. Asserted before
    // anything below, because a tab that did not mount makes the rest unreadable.
    for (const [path, where] of [
      [`/studio/${String(studio.id)}`, "the studio detail page"],
      [`/performer/${String(performer.id)}`, "the performer detail page"],
      [`/tag/${String(tag.json.id)}`, "the tag detail page"],
    ]) {
      await visit(page, baseUrl, path, hostDetailTabs(page), where);
      await expect(
        missingTab(page),
        `${where}: the host drew its own detail tabs and no ${TAB_LABEL} tab.`,
      ).toBeVisible({ timeout: TAB_BUDGET_MS });

      await missingTab(page).click();

      // Either cards or a stated reason, and never a blank region: a tab that mounted and drew
      // nothing is the failure a reader cannot tell from a catalogue that is genuinely empty.
      await expect
        .poll(async () => (await cards(page).count()) + (await statedReasons(page).count()), {
          timeout: REGION_BUDGET_MS,
          message: `${where}: the tab mounted and rendered neither a card nor a sentence.`,
        })
        .toBeGreaterThan(0);
    }

    const missingComponent = consoleErrors.filter((line) =>
      /component not found|does not provide an export|SyntaxError/i.test(line),
    );
    expect(
      missingComponent,
      `a page reported a component the bundle does not register: ${missingComponent.join(" | ")}`,
    ).toEqual([]);

    // Reported as SKIPPED rather than returned from. Everything below needs a catalogue this run
    // cannot read, and a test that returns here still reports a pass: the run then says this file
    // covered the older generation, the unknown-status readings and the stopped instance, none of
    // which it reached. A skip says what it did not do, in the run's own count.
    test.skip(providerSkip !== null, `no catalogue can be read: ${providerSkip}`);

    // A CONNECTED INSTANCE. The catalogue is read, the instance answers, and the page reports a
    // status it actually read.
    const connected = await readMissingPage(coveApi, "studio", studio.id);
    expect(
      connected.cards.length,
      "a provider and an instance were both configured, so the catalogue should have answered with cards",
    ).toBeGreaterThan(0);
    expect(
      connected.statusIsPermanentlyAbsent,
      "a connected instance of this generation keeps per-scene records, so nothing about the status is permanent",
    ).toBe(false);

    const firstCard = cards(page).first();
    await visit(
      page,
      baseUrl,
      `/studio/${String(studio.id)}`,
      missingTab(page),
      "the studio detail page",
    );
    await missingTab(page).click();
    await expect(firstCard, "the grid drew no card for a catalogue that answered").toBeVisible({
      timeout: REGION_BUDGET_MS,
    });
    await expect(
      firstCard.getByText(new RegExp(PILL_WORDS.join("|"))),
      "the first card carries no status pill in this product's own vocabulary",
    ).toBeVisible();

    // THE OLDER GENERATION. It identifies entities against the other metadata source, which this
    // build ships no client for, so the page states a reason rather than reaching a catalogue. What
    // is asserted here is that the tab is still present and still says something.
    await connectWhisparr(coveApi, whisparr, "v2");
    const older = await readMissingPage(coveApi, "studio", studio.id);
    expect(
      older.refusal,
      "the older generation answered no stated reason at all, so the tab would render a blank region",
    ).not.toBe("none");
    test.info().annotations.push({
      type: "narrowed-assertion",
      description:
        "the permanent absence over a full grid is asserted in the backend suite: this generation identifies against a metadata source this build has no client for, so its page read states that no provider is configured before any status is reached.",
    });

    // THE INSTANCE STOPPED. The catalogue still reads, so the grid is full; the instance answers
    // nothing, so every card reads the same four words the older generation's would. The field
    // beside them is what says a retry could change this one.
    await connectWhisparr(coveApi, whisparr, "v3");
    await whisparr.stop();
    whisparrStopped = true;

    const unreachable = await readMissingPage(coveApi, "studio", studio.id);
    expect(
      unreachable.cards.length,
      "the provider answered, so the catalogue below the notice is still complete",
    ).toBe(connected.cards.length);
    expect(
      unreachable.statusWasRead,
      "the instance was stopped, so no status can have been read",
    ).toBe(false);
    expect(
      unreachable.statusIsPermanentlyAbsent,
      "the instance was stopped rather than replaced, so a retry could still answer",
    ).toBe(false);
    expect(
      unreachable.refusal,
      "a stopped instance is the transient reason, not the permanent one",
    ).toBe("whisparrStatusNotRead");
    expect(
      unreachable.cards.map((card) => card.state),
      "the instance answered nothing, so every card's state is the unknown one",
    ).toEqual(unreachable.cards.map(() => "statusUnknown"));

    await visit(
      page,
      baseUrl,
      `/studio/${String(studio.id)}`,
      missingTab(page),
      "the studio detail page with the instance stopped",
    );
    await missingTab(page).click();
    await expect(cards(page).first(), "the grid emptied when the instance stopped").toBeVisible({
      timeout: REGION_BUDGET_MS,
    });
    await expect(
      cards(page).first().getByText(UNKNOWN_PILL),
      `the instance answered nothing, so the first card should read "${UNKNOWN_PILL}"`,
    ).toBeVisible();
  } finally {
    if (!whisparrStopped) await whisparr.stop();
  }
});

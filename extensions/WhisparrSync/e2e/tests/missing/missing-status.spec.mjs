// What a catalogue page says about the instance, in a real containerized host.
//
// WHY THIS SPEC EXISTS. The tab draws the same four words whether the connected instance answered
// and reported nothing, or cannot answer at all. Those are different facts and a reader acts on them
// differently: one clears on a retry and the other never will. The page carries the difference in
// its own fields, and this spec is the evidence that a real host and a real instance produce them.
//
// WHAT IS DRIVEN, AND WHAT IS READ. The tab is driven in the browser, because whether it mounts on
// each page type is only observable there. The refusal is read off the extension's own route
// through the host, because a pill's four words are the same string for two of the cases and the
// field beside them is what tells them apart.
//
// WHAT EACH GENERATION'S READ REACHES. Both sources are stood in for by a container answering to
// that source's own name on this installation's network, so this spec needs no credential on the
// machine running it. The two reach different distances, and the difference is a property of the
// product rather than of the stubs:
//
// Both generations take the catalogue from the connected instance, so the cards below come from
// what this spec seeded into it. The source stubs decide only whether the library's identifier
// resolves to something the instance can be asked about: a studio by its own id on v3, a site by
// its number on v2. Neither stub is ever asked for a catalogue, and the v2 arm asserts that.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { randomInt, randomUUID } from "node:crypto";

import { startWhisparr } from "@cove-extensions/e2e/whisparr";

import { WHISPARR_CATALOGUE_NOT_READ } from "../../../src/WhisparrSync.Ui/src/common/ui/copy.ts";
import { startMetadataStub } from "../../lib/metadata-stub.mjs";
import { seedV2Scene } from "../../lib/seed-scene.mjs";

import {
  cleanupStack,
  connectWhisparr,
  expect,
  extensionRoute,
  seedCovePerformer,
  seedCoveStudio,
  SPEC_BUDGET_MS,
  STASHDB_ENDPOINT,
  test,
  THEPORNDB_ENDPOINT,
} from "../../lib/connected-fixture.mjs";
import { visit } from "../../lib/steps.mjs";

// The tab's label and the four pill words, transcribed by hand from the shipped vocabulary. A spec
// importing the constants the product declares would be asserting that a string equals itself.
const TAB_LABEL = "Missing";
const UNKNOWN_PILL = "Status unknown";
/** The sentence the tab states when the instance could not be asked, with its slot filled out. */
const CATALOGUE_NOT_READ_SENTENCE = WHISPARR_CATALOGUE_NOT_READ.split("{entity}")[0];

const PILL_WORDS = ["Monitored", "Unmonitored", "Not added", UNKNOWN_PILL];

// A real StashDB studio with a real catalogue. The uuid is what Cove stores as its remote id and
// what the extension subtracts ownership on.
const BRAZZERS_EXXTRA = "39cee498-a9ac-4403-910a-1a0157ad22d8";

// Each budget names the operation it bounds, so a failure says which one blew it rather than
// reporting the whole test as a timeout naming nothing.
const TAB_BUDGET_MS = 30_000;
const REGION_BUDGET_MS = 90_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });
test.use({ generation: "v3", providers: ["stashdb", "theporndb"] });

/** The tab, by the only name the host draws it under. */
const missingTab = (page) => page.getByRole("tab", { name: TAB_LABEL }).first();

/** The host's own detail-tab strip, which tells a page that has rendered from one still loading. */
const hostDetailTabs = (page) => page.getByRole("tablist").first();

/** The cards the grid drew. */
const cards = (page) => page.locator("article").filter({ has: page.locator("img, h3") });

/** Any sentence the tab stated in place of a grid. */
const statedReasons = (page) => page.locator("p").filter({ hasText: /\S/ });

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
  isolatedCove,
  connected,
}) => {
  const { api: coveApi, provider, whisparr } = connected;

  // Everything the browser reported, so a bundle-load throw is named by this spec rather than left
  // as a blank region someone has to go and explain.
  const consoleErrors = [];
  page.on("console", (message) => {
    if (message.type() === "error") consoleErrors.push(message.text());
  });
  page.on("pageerror", (failure) => {
    consoleErrors.push(String(failure));
  });

  const cleanup = cleanupStack();
  let v3Stopped = false;
  try {
    // The other generation, started here rather than by the fixture: the fixture connects one, and
    // what this spec compares is two connections against one installation.
    //
    // Its site number is decided before the instance starts, because the instance reads the element
    // naming its metadata service once at startup and never again. Without that service this
    // generation resolves no site at all, and a catalogue read of one answers empty.
    const v2SiteId = randomInt(1, 1_000_001);
    // One title for all three rows: the Cove studio, the site the instance holds and the row the
    // metadata service answers a lookup with. The resolution matches them up, so a stub answering
    // under another name resolves to nothing and the catalogue reads empty.
    const v2SiteTitle = `V2 ${randomUUID().slice(0, 8)}`;
    const network = isolatedCove.container.getNetworkNames()[0];
    const v2Metadata = await startMetadataStub({
      networkName: network,
      sites: [{ tvdbId: v2SiteId, title: v2SiteTitle, titleSlug: String(v2SiteId) }],
    });
    cleanup.push("the v2 metadata stub", () => v2Metadata.stop());

    const v2Instance = await startWhisparr({
      network,
      generations: ["v2"],
      metadataUrl: v2Metadata.urlFromWhisparr,
    });
    cleanup.push("the v2 instance", () => v2Instance.stop());

    const studio = await seedCoveStudio(coveApi, {
      name: `Brazzers Exxtra ${randomUUID().slice(0, 8)}`,
      remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: BRAZZERS_EXXTRA }],
    });

    // The instance is what lists a studio's scenes now, so one it does not hold lists none and the
    // tab states that instead of drawing a grid. Seeded under the identifier the library carries,
    // which is what the extension resolves the Cove studio to.
    await whisparr.seedEntity("v3", {
      kind: "studio",
      foreignId: BRAZZERS_EXXTRA,
      title: studio.name,
    });

    // The scenes the catalogue lists. A studio the instance holds with no works answers an empty
    // catalogue, which reads the same as a studio it does not hold at all.
    const catalogue = [
      { foreignId: `${BRAZZERS_EXXTRA}-a`, title: `Catalogue Scene A ${studio.name}` },
      { foreignId: `${BRAZZERS_EXXTRA}-b`, title: `Catalogue Scene B ${studio.name}` },
    ];
    for (const scene of catalogue) {
      await whisparr.seedEntity("v3", {
        kind: "scene",
        foreignId: scene.foreignId,
        title: scene.title,
        studioForeignId: BRAZZERS_EXXTRA,
        studioTitle: studio.name,
      });
    }
    const performer = await seedCovePerformer(coveApi, {
      name: `Performer ${randomUUID().slice(0, 8)}`,
      remoteIds: [],
    });
    const tag = await coveApi.post("/api/tags", { name: `Tag ${randomUUID().slice(0, 8)}` });
    expect(tag.status, `POST /api/tags answered ${String(tag.status)}`).toBeLessThan(300);

    // Every page type renders the tab and none of them reports a missing component. Asserted before
    // anything below, because a tab that did not mount makes the rest unreadable.
    // No tag page: a tag names no catalogue anyone could ask an instance about, so this product
    // registers no tab for one.
    for (const [path, where] of [
      [`/studio/${String(studio.id)}`, "the studio detail page"],
      [`/performer/${String(performer.id)}`, "the performer detail page"],
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

    // A CONNECTED INSTANCE. The catalogue is read, the instance answers, and the page draws cards
    // rather than stating a reason.
    const connectedPage = await readMissingPage(coveApi, "studio", studio.id);
    expect(
      connectedPage.cards.length,
      "a provider and an instance were both configured, so the catalogue should have answered with cards",
    ).toBeGreaterThan(0);

    // The cards are evidence about this product only if they came from the catalogue this spec
    // seeded. Read against the instance's own works: this product asks the instance for what a
    // studio lists, and the metadata stub is configured here only so identifiers resolve.
    expect(
      connectedPage.cards.map((card) => card.title),
      `no card carries a title this spec seeded, so the grid is drawing a catalogue it did not serve: ${catalogue.map((one) => one.title).join(", ")}`,
    ).toContain(catalogue[0].title);

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

    // V2. Its source is registered here like the other one, so the read resolves a provider and
    // gets past the gate that answers when none is. What it then reaches is the limit stated at the
    // head of this file, and the stub's own log below is the evidence for it.
    await connectWhisparr(coveApi, v2Instance, "v2");
    // A site the v2 instance holds, under the number the library carries. This generation lists a
    // site's scenes from the instance, so a site it does not hold answers that it holds none before
    // anything about a source is reached, and the reason under test here is never raised.
    const v2Studio = await seedCoveStudio(coveApi, {
      name: v2SiteTitle,
      remoteIds: [{ endpoint: THEPORNDB_ENDPOINT, remoteId: String(v2SiteId) }],
    });
    await seedV2Scene(v2Instance.v2.container, v2Instance.apiFor("v2"), {
      siteId: v2SiteId,
      siteTitle: v2SiteTitle,
      rootFolderPath: v2Instance.v2.rootFolder,
      sceneExternalId: randomUUID(),
      sceneTitle: `V2 catalogue scene ${v2Studio.name}`,
    });
    const v2Page = await readMissingPage(coveApi, "studio", v2Studio.id);
    expect(
      v2Page.refusal,
      `v2 stated a reason for a site its instance holds: ${String(v2Page.refusal)}`,
    ).toBe("none");

    test.info().annotations.push({
      type: "narrowed-assertion",
      description:
        "the number of scenes v2 lists for the site is not asserted. This suite has no recipe that produces a listed v2 catalogue: every card-level spec runs on v3, and a site seeded with a scene reads back an empty catalogue here. What is asserted is the refusal this spec exists for, which the read answers either way. The projection over a v2 catalogue is covered in the backend suite.",
    });

    // The stub standing in for v2's source is never asked, and that is the point: this generation
    // takes its catalogue from the instance, so the source decides only whether the library's
    // identifier resolves to a site number at all.
    expect(
      await provider.theporndb.asked(),
      "the stub standing in for v2's source was asked for a catalogue, so this spec's account of where v2 reads one is out of date",
    ).toEqual([]);

    // THE INSTANCE STOPPED. The instance is where the catalogue comes from, so stopping it leaves
    // no catalogue to draw and no card to carry a status. The page says which of the two happened:
    // the scenes could not be read at all, and a retry could still answer.
    await connectWhisparr(coveApi, whisparr, "v3");
    await whisparr.stop();
    v3Stopped = true;

    const unreachable = await readMissingPage(coveApi, "studio", studio.id);
    expect(
      unreachable.refusal,
      `a stopped instance is the transient reason, not the permanent one: ${JSON.stringify(unreachable).slice(0, 300)}`,
    ).toBe("whisparrCatalogueNotRead");
    expect(
      unreachable.cards.length,
      "the instance was stopped, so there is nothing to list its scenes from",
    ).toBe(0);

    // The same fact in the browser: a stated reason in place of a grid, never a blank region.
    await visit(
      page,
      baseUrl,
      `/studio/${String(studio.id)}`,
      missingTab(page),
      "the studio detail page with the instance stopped",
    );
    await missingTab(page).click();
    await expect(
      page.getByText(CATALOGUE_NOT_READ_SENTENCE, { exact: false }),
      "the tab drew neither a grid nor a reason, so the reader is told nothing at all",
    ).toBeVisible({ timeout: REGION_BUDGET_MS });
    await expect(
      cards(page),
      "the instance was stopped, so the grid has nothing to draw",
    ).toHaveCount(0);
  } finally {
    // Stopping the v3 instance is part of what this spec drives, and the fixture registered a
    // stop for it too. Withdrawing that one keeps the unwind from reporting a container it cannot
    // find, which would read as a teardown fault on a passing run.
    if (v3Stopped) {
      whisparr.stop = async () => {};
    }
    await cleanup.unwind();
  }
});

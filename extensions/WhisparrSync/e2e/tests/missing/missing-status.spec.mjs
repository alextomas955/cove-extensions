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
// WHAT EACH GENERATION'S READ REACHES. Both sources are stood in for by a container answering to
// that source's own name on this installation's network, so this spec needs no credential on the
// machine running it. The two reach different distances, and the difference is a property of the
// product rather than of the stubs:
//
// - V3 posts its catalogue query to the resolved identity endpoint, so the stub standing in for
//   that source IS the catalogue, and the cards below come from it.
// - V2 resolves the same way and then reads its catalogue over REST from a compiled-in address, so
//   registering its source decides only whether a provider is found. That is the difference
//   asserted at the v2 arm below: registered, the read names the provider and states it could not
//   be reached; unregistered, it states that none is configured. The catalogue itself is out of
//   reach of any stub this suite starts.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { randomUUID } from "node:crypto";

import { startWhisparr } from "@cove-extensions/e2e/whisparr";

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
const PILL_WORDS = ["Wanted", "Unmonitored", "Not added", UNKNOWN_PILL];

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
    const v2Instance = await startWhisparr({
      network: isolatedCove.container.getNetworkNames()[0],
      generations: ["v2"],
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

    // A CONNECTED INSTANCE. The catalogue is read, the instance answers, and the page reports a
    // status it actually read.
    const connectedPage = await readMissingPage(coveApi, "studio", studio.id);
    expect(
      connectedPage.cards.length,
      "a provider and an instance were both configured, so the catalogue should have answered with cards",
    ).toBeGreaterThan(0);

    // Read off the stub's own record: the cards below are evidence about this product only if the
    // page they came from is the one this spec served.
    expect(
      (await provider.stashdb.asked()).filter((line) => line.includes("MissingPage")),
      "the stub was never asked for a page, so the grid is drawing something this spec did not serve",
    ).not.toEqual([]);
    expect(
      connectedPage.statusIsPermanentlyAbsent,
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

    // V2. Its source is registered here like the other one, so the read resolves a provider and
    // gets past the gate that answers when none is. What it then reaches is the limit stated at the
    // head of this file, and the stub's own log below is the evidence for it.
    await connectWhisparr(coveApi, v2Instance, "v2");
    const v2Studio = await seedCoveStudio(coveApi, {
      name: `V2 ${randomUUID().slice(0, 8)}`,
      remoteIds: [{ endpoint: THEPORNDB_ENDPOINT, remoteId: String(Date.now()) }],
    });
    const v2Page = await readMissingPage(coveApi, "studio", v2Studio.id);
    expect(
      v2Page.refusal,
      "v2 answered no stated reason at all, so the tab would render a blank region",
    ).not.toBe("none");
    expect(
      v2Page.refusal,
      "v2's read did not resolve the source registered for it: it states that none is configured, which is the answer this stub exists to move past",
    ).toBe("providerUnreachable");
    expect(
      await provider.theporndb.asked(),
      "the stub standing in for v2's source WAS asked, so the catalogue read now consults the configuration and this spec's account of what it reaches is out of date",
    ).toEqual([]);
    test.info().annotations.push({
      type: "narrowed-assertion",
      description:
        "v2's catalogue is not read here, and no stub can serve it: its client builds every request against a compiled-in address on the open internet and never consults the configuration. What is asserted is that the source resolves and the unreachable catalogue is stated as such. The projection over that catalogue is covered in the backend suite.",
    });

    // THE INSTANCE STOPPED. The catalogue still reads, so the grid is full; the instance answers
    // nothing, so every card reads the same four words v2's would. The field
    // beside them is what says a retry could change this one.
    await connectWhisparr(coveApi, whisparr, "v3");
    await whisparr.stop();
    v3Stopped = true;

    const unreachable = await readMissingPage(coveApi, "studio", studio.id);
    expect(
      unreachable.cards.length,
      "the provider answered, so the catalogue below the notice is still complete",
    ).toBe(connectedPage.cards.length);
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
    // Stopping the v3 instance is part of what this spec drives, and the fixture registered a
    // stop for it too. Withdrawing that one keeps the unwind from reporting a container it cannot
    // find, which would read as a teardown fault on a passing run.
    if (v3Stopped) {
      whisparr.stop = async () => {};
    }
    await cleanup.unwind();
  }
});

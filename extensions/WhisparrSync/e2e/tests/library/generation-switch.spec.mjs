// What changing the connected generation does to the surfaces on the page, in a real containerized
// host.
//
// WHY THIS SPEC EXISTS. Which surfaces this extension registers is decided by the generation the
// connection names, and the browser learns the set from a manifest it fetches on load. Nothing
// pushes a new one. So the whole gate rests on a reload reading a manifest built from the other
// generation, and only a run that switches the connection and reloads can say whether it does.
//
// WHY IT IS ITS OWN FILE. The connected generation is a global extension setting. A test that
// changes it cannot share an installation with tests that expect a fixed connection, and it cannot
// sit beside them in a file that declares one generation for all of it.
//
// WHY BOTH INSTANCES ARE STARTED AT ONCE. What is measured is the difference between two
// connections. A run that started one of them only would compare a generation with itself.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { randomUUID } from "node:crypto";

import { registerRootFolder, startWhisparr } from "@cove-extensions/e2e/whisparr";

import {
  cleanupStack,
  connectWhisparr,
  expect,
  EXTENSION_ID,
  seedCovePerformer,
  seedCoveStudio,
  seedCoveVideo,
  SETTLE_DWELL_MS,
  SPEC_BUDGET_MS,
  STASHDB_ENDPOINT,
  test,
  THEPORNDB_ENDPOINT,
  WHISPARR_ROOT,
} from "../../lib/connected-fixture.mjs";
import { visit } from "../../lib/steps.mjs";

/** The control's two names, transcribed by hand from the shipped sentences. */
const SHOW_STATUS = "Show Whisparr status";
const HIDE_STATUS = "Hide Whisparr status";

/**
 * The five words a badge may read, transcribed by hand from the shipped vocabulary.
 *
 * A spec importing the constants the product declares would be asserting that a string equals
 * itself.
 */
const STATE_WORDS = ["Monitored", "Unmonitored", "Not added", "Excluded", "Status unknown"];

const GRID_BUDGET_MS = 60_000;
const BADGE_BUDGET_MS = 90_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });

/**
 * The control, by the only name it has, in either state.
 *
 * Its accessible name carries a reason after it when there is one, so the match is anchored at the
 * front rather than exact.
 */
const statusToggle = (page) =>
  page.getByRole("button", { name: new RegExp(`^(${SHOW_STATUS}|${HIDE_STATUS})`) });

/**
 * The host's own in-card extension box, which it renders only where something registers for that
 * card's slot.
 *
 * This extension is the only one installed, so a count of zero over a page of cards is the
 * registration being absent and not a component drawing nothing.
 */
const cardExtensionBoxes = (scope) => scope.locator(".card-extension");

/** Every card the studios grid drew. */
const studioCards = (page) => page.locator(".entity-card");

/** One studio's own card, found by the name it was seeded under. */
const cardFor = (page, name) => studioCards(page).filter({ hasText: name });

/** Every card the videos grid drew, which the host gives its own class rather than the entity one. */
const videoCards = (page) => page.locator(".video-card");

/** One scene's own card, found by the title it was seeded under. */
const videoCardFor = (page, title) => videoCards(page).filter({ hasText: title });

/**
 * Every state chip inside a card's own extension box.
 *
 * The chip's label shares its element with a drawn mark, so the element carries both and an
 * exact-text locator finds nothing. The shape is located here and the words are asserted off it.
 */
const stateChips = (scope) => scope.locator(".card-extension span.rounded-full");

/** The words a chip may read, as the element carries them: a glyph, then the label. */
const STATE_CHIP_TEXT = new RegExp(`(${STATE_WORDS.join("|")})$`);

/** Opens the studios grid and waits for the cards and the control to be on screen. */
async function openStudios(page, baseUrl) {
  await visit(page, baseUrl, "/studios", studioCards(page).first(), "the studios page");
  await expect(statusToggle(page), "the toolbar drew no Whisparr status control").toBeVisible({
    timeout: GRID_BUDGET_MS,
  });
}

/** Opens a list page in its default grid display mode, with the cards and the control on screen. */
async function openList(page, baseUrl, path, cards, where) {
  await visit(page, baseUrl, path, cards.first(), where);
  await expect(
    statusToggle(page),
    `${where}: the toolbar drew no Whisparr status control`,
  ).toBeVisible({ timeout: GRID_BUDGET_MS });
}

/**
 * Opens a list page in its default grid display mode with its cards on screen, expecting no control
 * of this extension's.
 *
 * Held apart from `openList`, which waits for the control: a page the control is absent from is what
 * this asks about, so waiting for it would time out before anything was read.
 */
async function openListWithoutTheControl(page, baseUrl, path, cards, where) {
  await visit(page, baseUrl, path, cards.first(), where);
  await page.waitForTimeout(SETTLE_DWELL_MS);
}

/**
 * Every slot this extension registers in the manifest the browser is served.
 *
 * The DOM cannot report a slot the host renders no element for at all, which is the host's own
 * full-width row below a list toolbar, so that one is read from the registration the page was built
 * from.
 */
async function registeredSlots(api) {
  const manifest = await api.get("/api/extensions/manifest");
  expect(manifest.status, `GET the extension manifest answered ${String(manifest.status)}`).toBe(
    200,
  );
  return (manifest.json?.slots ?? [])
    .filter((entry) => entry.extensionId === EXTENSION_ID)
    .map((entry) => entry.slot);
}

/**
 * Seeds one scene on both sides: the Cove video carrying its identity row, and the entry the
 * instance holds for it.
 *
 * The instance's entry is written into its own datastore rather than added through its API: an add
 * resolves the identifier against the vendor's metadata service, so a scene's mere existence would
 * depend on someone else's uptime.
 */
async function seedScene(coveApi, whisparr, { label, monitored = false }) {
  const remoteId = randomUUID();
  await whisparr.seedEntity("v3", {
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
  return { ...video, title, remoteId };
}

test("the videos and performers surfaces are absent on v2, and return when it is switched away from", async ({
  page,
  baseUrl,
  api: coveApi,
  isolatedCove,
}) => {
  const cleanup = cleanupStack();
  try {
    const whisparr = await startWhisparr({
      network: isolatedCove.container.getNetworkNames()[0],
      generations: ["v3", "v2"],
    });
    cleanup.push("both instances", () => whisparr.stop());
    whisparr.v3.rootFolder = await registerRootFolder(
      whisparr.v3.container,
      whisparr.apiFor("v3"),
      "v3",
      WHISPARR_ROOT,
    );
    await connectWhisparr(coveApi, whisparr, "v2");

    const video = await seedScene(coveApi, whisparr, {
      label: "Switched",
      monitored: true,
    });
    const performerId = randomUUID();
    await seedCovePerformer(coveApi, {
      name: `Performer ${performerId.slice(0, 8)}`,
      remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: performerId }],
    });

    // The registration is what removes the surface, so the set the page was built from is read
    // before the page is. The host renders no element at all for its full-width row slot, so that
    // one is only assertable here.
    const onV2 = await registeredSlots(coveApi);
    expect(onV2.sort(), "v2 registers a slot for a surface it has no meaning for").toEqual(
      [
        "studio-card-footer",
        "studio-detail-actions",
        "studios-list-toolbar-end",
        "studios-list-row",
      ].sort(),
    );
    // The row goes with the card badges it counts. The studio badges are registered on both
    // generations, so the studios row is too, and the pages with no badge have no row.
    expect(
      onV2.filter((slot) => slot.endsWith("-list-row")),
      "a page with no card badge carries a row of counts over nothing",
    ).toEqual(["studios-list-row"]);

    for (const [path, cards, where] of [
      ["/videos", videoCards(page), "the videos page"],
      ["/performers", studioCards(page), "the performers page"],
    ]) {
      await openListWithoutTheControl(page, baseUrl, path, cards, where);

      await expect(statusToggle(page), `${where}: v2 drew a Whisparr status control`).toHaveCount(
        0,
      );
      await expect(
        cardExtensionBoxes(page),
        `${where}: v2 drew the host's in-card extension box, so a surface renders empty rather than being absent`,
      ).toHaveCount(0);
    }

    // The studios page keeps both on this generation: a studio monitors as a series matched by
    // ThePornDB there, so its id is written under that source.
    const studio = await seedCoveStudio(coveApi, {
      name: `V2 ${randomUUID().slice(0, 8)}`,
      remoteIds: [{ endpoint: THEPORNDB_ENDPOINT, remoteId: randomUUID() }],
    });

    await openStudios(page, baseUrl);
    await statusToggle(page).click();

    // The registration mounted, which is the same fact read as an absence above and the reason
    // the two pages differ on one connection. The host draws this box for a registered card slot
    // whatever the component inside it returns.
    await expect(
      cardExtensionBoxes(cardFor(page, studio.name)),
      "v2 drew no in-card extension box on a studio card, so the studio surfaces went with the videos ones",
    ).toHaveCount(1, { timeout: BADGE_BUDGET_MS });

    test.info().annotations.push({
      type: "narrowed-assertion",
      description:
        "the studio card's own state chip is not asserted on this generation: its read resolves the stored identifier through the vendor's metadata service before it reaches the instance, which no container run can reach, so a read that established nothing draws nothing by design. What is asserted here is that the studio slots are registered and mounted on this generation, which is the half the generation gate decides.",
    });

    // Switched back and reloaded. The browser fetches the manifest and nothing pushes it, so this
    // is the mechanism the whole gate depends on.
    await connectWhisparr(coveApi, whisparr, "v3");

    const onV3 = await registeredSlots(coveApi);
    expect(
      onV3.filter((slot) => !onV2.includes(slot)).sort(),
      "switching the connection back changed no registration, so the manifest is not re-read",
    ).toEqual(
      [
        "performer-card-footer",
        "performers-list-toolbar-end",
        "performers-list-row",
        "video-card-content",
        "videos-list-toolbar-end",
        "videos-list-row",
      ].sort(),
    );

    await openList(page, baseUrl, "/videos", videoCards(page), "the videos page");
    await statusToggle(page).click();

    const videoChip = stateChips(videoCardFor(page, video.title));
    await expect(
      videoChip,
      "the videos surfaces did not return after the connection was switched back and the page reloaded",
    ).toHaveCount(1, { timeout: BADGE_BUDGET_MS });
    await expect(
      videoChip,
      "the scene's chip reads something outside this product's own five-state vocabulary",
    ).toHaveText(STATE_CHIP_TEXT);
  } finally {
    await cleanup.unwind();
  }
});

// v2 draws no Whisparr button on the videos selection bar, in a real containerized host.
//
// The absence comes from the registration rather than from a component. On this generation the
// videos bulk action never reaches the manifest the host serves, so the selection bar carries
// neither this extension's button nor the host's own button for a contributed action with nothing
// in it. Those are different DOM states, and only the first is what this generation promises.
//
// The manifest is read before the page, because the host draws no selection bar at all while
// nothing is selected, so an absent button and an absent registration look alike from the page.
//
// IF THIS SPEC GOES RED, read the run log for a container-not-running line before debugging the UI.
// A red end-to-end run in this repository is usually the Cove container dying rather than the page
// under test.
import { randomUUID } from "node:crypto";

import {
  expect,
  EXTENSION_ID,
  seedCoveVideo,
  SETTLE_DWELL_MS,
  SPEC_BUDGET_MS,
  STASHDB_ENDPOINT,
  test,
} from "../../lib/connected-fixture.mjs";
import { visit } from "../../lib/steps.mjs";

// The button's label, transcribed by hand from the registration that declares it. A spec importing
// the same constant would be asserting that a string equals itself.
const BATCH_BUTTON_LABEL = "Whisparr";

// The spelling the host's videos selection bar passes, and the one a scene bulk action has to
// declare to be matched. Transcribed by hand from the registration, like every literal here.
const VIDEOS_SELECTION_TYPE = "video";

// The budget the page's own rendering is bounded by, so a failure says which operation blew it.
const PAGE_BUDGET_MS = 60_000;

test.describe.configure({ timeout: SPEC_BUDGET_MS });
test.use({ generation: "v2" });

const batchButton = (page) => page.getByRole("button", { name: BATCH_BUTTON_LABEL, exact: true });

/**
 * Every contributed selection-action button the host drew, by the glyph it draws on all of them.
 *
 * The empty-versus-absent distinction in its selection-bar form. The host draws this button for a
 * registered bulk action whatever the extension declares, and draws nothing at all where no action
 * matched, so a non-zero count here is a surface that rendered rather than one that is absent.
 * Scoped inside the page's own main region, which the navigation is not.
 */
const contributedSelectionButtons = (page) =>
  page.locator("main").locator("button:has(svg.lucide-puzzle)");

/** Every card's own selection toggle on a list page, in DOM order. */
const cardToggles = (page) => page.getByRole("button", { name: /^(Select|Deselect) item$/ });

/** Every bulk action this extension registers for a video selection in the served manifest. */
async function registeredVideoBulkActions(api) {
  const manifest = await api.get("/api/extensions/manifest");
  expect(manifest.status, `GET the extension manifest answered ${String(manifest.status)}`).toBe(
    200,
  );
  return (manifest.json?.actions ?? [])
    .filter(
      (entry) =>
        entry.extensionId === EXTENSION_ID &&
        entry.actionType === "bulk" &&
        (entry.entityTypes ?? []).includes(VIDEOS_SELECTION_TYPE),
    )
    .map((entry) => entry.id);
}

/**
 * Selects the first `count` cards, addressing each by position and reading the selection back.
 *
 * Positional rather than by label, because the label a card carries depends on the state the
 * previous click left it in. A selection that did not take is otherwise indistinguishable from a
 * button the host declined to render, and only one of those is this spec's subject.
 */
async function selectFirstCards(page, count, where) {
  const toggles = cardToggles(page);
  await expect(
    toggles.first(),
    `${where}: no selectable card rendered within ${String(PAGE_BUDGET_MS)}ms, so nothing could be selected`,
  ).toBeVisible({ timeout: PAGE_BUDGET_MS });

  for (let index = 0; index < count; index++) {
    await toggles.nth(index).click();
  }

  await expect(
    page.getByRole("button", { name: "Deselect item" }),
    `${where}: the cards do not report ${String(count)} selected after ${String(count)} click(s), so the selection this spec needs never happened and nothing below would be about the extension`,
  ).toHaveCount(count);
}

test("v2 draws no Whisparr button on the videos selection bar, and no wrapper for one either", async ({
  page,
  baseUrl,
  connected,
}) => {
  const { api: coveApi } = connected;

  // No entry on the instance and none needed. Nothing is asked of it on this generation, and a
  // seeded entry would make an absent button look like a button with nothing to say.
  await seedCoveVideo(coveApi, {
    title: `V2 ${randomUUID().slice(0, 8)}`,
    remoteIds: [{ endpoint: STASHDB_ENDPOINT, remoteId: randomUUID() }],
  });

  expect(
    await registeredVideoBulkActions(coveApi),
    "v2 registers a videos selection action, so a surface it has no meaning on reached the manifest the host served",
  ).toEqual([]);

  await visit(page, baseUrl, "/videos", cardToggles(page).first(), "the videos page");
  await selectFirstCards(page, 1, "the videos page on v2");
  await page.waitForTimeout(SETTLE_DWELL_MS);

  await expect(
    batchButton(page),
    `v2 drew a ${BATCH_BUTTON_LABEL} button on the videos selection bar, so the registration is not conditional on the stored generation`,
  ).toHaveCount(0);

  await expect(
    contributedSelectionButtons(page),
    "the host drew its own button for a contributed selection action, so this surface renders empty rather than being absent",
  ).toHaveCount(0);
});

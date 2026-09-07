// Drives the Missing tab's precedence rule — which reason the SURFACE states when more than one guard applies —
// through a real browser on both Whisparr generations. A control's `title` has carried the capability reason over
// the configuration one for a while; the assertion here is that the rendered line obeys the same order, and that a
// whole set of rows abstaining for one cause says so once rather than once per card.
//
// Both generations are driven HERMETICALLY: the harness stands up no Whisparr, so `/discovery/entity` is fulfilled
// with a synthetic body whose `version` selects the generation and whose per-row `status` selects the abstention.
// The configuration fact underneath is REAL — each leg writes the extension's own stored options and lets
// `/status` answer from the server's own predicate — because a spec that intercepted both would be asserting
// against its own fixture rather than against the guard.
//
// Every count is a RULE, never a fixture number: the card count is declared here, and each expectation is written
// as "once whatever the count", "equal to the count", or "zero". The dev fixture's Postgres is a tmpfs, so a
// reseeded library must not be able to break a case. And every zero is paired with a one on the same page — a
// case asserting only an absence passes on a blank page, which is the failure mode a suppression invites.
import { test, expect, seedCorpus, EXTENSION_ID } from "../lib/whisparrsync-fixtures.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

// Above the five the acceptance names, and below a page, so one page renders every card and no count assertion
// has to reason about pagination.
const CARD_COUNT = 12;

// A synthetic address, so the "met" leg stores a genuinely present value.
const STORED_ADDRESS = "http://refusal-precedence.invalid:6969";
// The unset state the configuration guard exists to refuse.
const NO_ADDRESS = "";

// The configuration sentence's shipped opening, taken from config-guard.spec.mjs's constant rather than retyped.
const REASON_SETTING = /Set the Whisparr URL in Whisparr Sync settings \(Connection\)/;
// The capability copy, single-sourced in the bundle as VERSION_CAPABILITY_COPY.
const CAPABILITY_COPY = "Currently available on Whisparr v3 (Eros)";
// The set-wide abstention's own opening — what distinguishes it from every other status region on the tab.
const SET_NOTICE = /Monitor, Unmonitor and Search act on a scene-level Whisparr row/;
// The per-card abstention sentence, which the set-wide statement replaces.
const PER_CARD_ABSTENTION = "The connected Whisparr can't report a per-scene status here";

function syntheticScenes(n, status) {
  const base = Date.UTC(2018, 0, 1);
  return Array.from({ length: n }, (_, i) => {
    const pad = String(i + 1).padStart(4, "0");
    return {
      sourceId: `synthetic-${pad}`,
      title: `Scene ${pad}`,
      releaseDate: new Date(base + (i + 1) * 86_400_000).toISOString().slice(0, 10),
      entityName: "Synthetic Studio",
      studioName: "Synthetic Studio",
      posterUrl: null,
      coverUrl: null,
      status,
    };
  });
}

/** Feeds the browser one generation's populated Missing list; the real endpoint answers empty with no Whisparr. */
async function routeGeneration(page, version, status) {
  const scenes = syntheticScenes(CARD_COUNT, status);
  await page.route("**/discovery/entity", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        scenes,
        entityName: "Synthetic Studio",
        state: "ok",
        source: version === "v2" ? "tpdb" : "stashdb",
        version,
      }),
    }),
  );
  await page.route("**/discovery/count**", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ count: scenes.length }),
    }),
  );
}

/**
 * Writes a COMPLETE stored connection, varying only the address, then reads the server's own verdict back —
 * `WithSubmitted` rebuilds the saved connection on write, so a partial body loses a field.
 */
async function storeAddress(api, baseUrl, selectedVersion) {
  const res = await api.post(`/api/extensions/${EXTENSION_ID}/options`, {
    BaseUrl: baseUrl,
    ApiKey: "refusal-precedence-spec-key",
    SelectedVersion: selectedVersion,
    TagsOnAdd: ["cove"],
    MonitorNewByDefault: true,
    AllowQualityUpgrades: false,
  });
  expect(res.status, `storing the options succeeded (body: ${res.text})`).toBeLessThan(300);
  const status = await api.get(`/api/extensions/${EXTENSION_ID}/status`);
  expect(status.status).toBe(200);
  expect(status.json.missingRequiredOptions).toEqual(baseUrl === NO_ADDRESS ? ["baseUrl"] : []);
}

async function openMissingTab({ harness, baseUrl, page }) {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId, "seedCorpus should link at least one scene to a studio").not.toBeUndefined();

  const detail = new EntityDetailPage(page, baseUrl);
  await detail.gotoStudio(studioId);
  const missingTab = page.getByRole("tab", { name: /Missing/ });
  await expect(missingTab).toBeVisible();
  await missingTab.click();
  await expect(missingTab).toHaveAttribute("aria-selected", "true");
  await expect.poll(() => page.locator('[role="listitem"] .video-card').count()).toBe(CARD_COUNT);
}

/** How many status regions on the page match a pattern — a count, never a presence check. */
function statusCount(page, pattern) {
  return page.getByRole("status").filter({ hasText: pattern }).count();
}

/** Every `title` attribute on the page, so an assertion about rendered copy cannot be satisfied by a tooltip. */
async function titles(page) {
  return page.locator("[title]").evaluateAll((els) => els.map((e) => e.getAttribute("title") ?? ""));
}

test("on a generation with no scene-level row the tab states the capability cause, not the setting", async ({
  harness,
  baseUrl,
  page,
  api,
}, testInfo) => {
  await storeAddress(api, NO_ADDRESS, "v2");
  await routeGeneration(page, "v2", "unknown");
  await openMissingTab({ harness, baseUrl, page });

  // The advice a setting cannot act on is absent from the surface — rendered AND as a tooltip, because moving it
  // into an attribute would satisfy a naive count while leaving the same claim on the screen.
  await expect.poll(() => statusCount(page, REASON_SETTING)).toBe(0);
  expect((await titles(page)).filter((t) => REASON_SETTING.test(t))).toEqual([]);

  // The pairing: the same page positively states the reason that DID win, once, whatever the card count.
  await expect.poll(() => statusCount(page, SET_NOTICE)).toBe(1);
  const notice = await page.getByRole("status").filter({ hasText: SET_NOTICE }).first().innerText();
  expect(notice).toContain(CAPABILITY_COPY);
  expect(notice).toContain(PER_CARD_ABSTENTION);
  // A standing limitation offers no retry, so the notice carries no control at all.
  await expect(
    page.getByRole("status").filter({ hasText: SET_NOTICE }).getByRole("button"),
  ).toHaveCount(0);

  // No card repeats it.
  expect((await titles(page)).filter((t) => t.includes(PER_CARD_ABSTENTION))).toEqual([]);

  // The glyph's label survived on every card, and each card's Monitor still explains itself.
  await expect(page.getByText("Status unknown", { exact: true })).toHaveCount(CARD_COUNT);
  const monitors = page.getByRole("button", { name: /^Mark this scene wanted/ });
  await expect(monitors).toHaveCount(CARD_COUNT);
  for (const name of await monitors.evaluateAll((els) =>
    els.map((e) => e.getAttribute("aria-label") ?? ""),
  )) {
    expect(name.startsWith("Mark this scene wanted")).toBe(true);
    expect(name).toContain(CAPABILITY_COPY);
  }

  await page.screenshot({ path: testInfo.outputPath("missing-v2-capability.png"), fullPage: true });
});

test("on a generation that offers the verbs the unmet setting is still stated, once, and only while unmet", async ({
  harness,
  baseUrl,
  page,
  api,
}, testInfo) => {
  await routeGeneration(page, "v3", "notAdded");

  // ---- (1) the option unmet: the sentence is the surface's one statement, and no capability notice appears ----
  await storeAddress(api, NO_ADDRESS, "v3");
  await openMissingTab({ harness, baseUrl, page });
  await expect.poll(() => statusCount(page, REASON_SETTING)).toBe(1);
  await expect.poll(() => statusCount(page, SET_NOTICE)).toBe(0);
  await page.screenshot({ path: testInfo.outputPath("missing-v3-unmet.png"), fullPage: true });

  // ---- (2) the option met: the sentence goes and the notice still never appears ----
  // The enable half is what stops a blanket suppression passing leg (1) by rendering nothing at all.
  await storeAddress(api, STORED_ADDRESS, "v3");
  await openMissingTab({ harness, baseUrl, page });
  await expect.poll(() => statusCount(page, REASON_SETTING)).toBe(0);
  await expect.poll(() => statusCount(page, SET_NOTICE)).toBe(0);
  // The pairing for this leg: the page is genuinely rendered, and its verbs are live.
  const monitors = page.getByRole("button", { name: /^Mark this scene wanted$/ });
  await expect(monitors).toHaveCount(CARD_COUNT);
  await expect(monitors.first()).toBeEnabled();
  await page.screenshot({ path: testInfo.outputPath("missing-v3-met.png"), fullPage: true });
});

test("a reportable generation keeps its per-card abstention sentences", async ({
  harness,
  baseUrl,
  page,
  api,
}) => {
  await storeAddress(api, STORED_ADDRESS, "v3");
  await routeGeneration(page, "v3", "unknown");
  await openMissingTab({ harness, baseUrl, page });

  // Where a status COULD have been reported, an abstention is a transient outage: the set banner keeps its
  // Refresh and the per-card sentences stay, neither replaced by the standing-limitation notice.
  await expect.poll(() => statusCount(page, SET_NOTICE)).toBe(0);
  expect((await titles(page)).filter((t) => t.includes(PER_CARD_ABSTENTION))).toEqual([]);
  const outageTitles = (await titles(page)).filter((t) => t.includes("Whisparr isn't reachable"));
  expect(outageTitles).toHaveLength(CARD_COUNT);
  await expect(page.getByText("Status unknown", { exact: true })).toHaveCount(CARD_COUNT);
});

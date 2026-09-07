// Drives the configuration-completeness guard's ENABLED/DISABLED split through a real browser against the
// running host, on the one thing a click path can prove and an offline gate cannot: that the guard dims exactly
// the controls which need a stored connection and leaves their siblings live. The pairing is the assertion — a
// spec asserting only the disabled state would pass just as happily for a blanket refusal, which is a worse
// product than no guard at all.
//
// The configuration fact is REAL, not intercepted: each case writes the extension's own stored options through
// its `/options` route (a complete body — a partial write rebuilds the saved connection and would blank a field)
// and lets `/status` answer from the server's own predicate. Only the Whisparr-dependent reads each surface
// needs to render at all are route-intercepted, with a small SYNTHETIC list (numeric "Scene NNNN" titles,
// fabricated cover urls — content-safe), because the hermetic harness stands up no Whisparr.
//
// Deliberately absent: any CSS class, pixel size, or pure-wording expectation. The sentence's exact literal is
// pinned by the `config-guard-logic` offline gate; what is asserted here is that it reaches the accessibility
// tree as a RENDERED node rather than living only in a title attribute — and HOW MANY TIMES. The counts are
// exact, never a presence check: the fact belongs to the connection, so it belongs on a surface once, and
// `toBeVisible` on `.first()` passed just as happily when it rendered forty times.
import { test, expect, seedCorpus, EXTENSION_ID } from "../lib/whisparrsync-fixtures.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";
import { ScenePanel } from "../lib/pages/scene-panel.mjs";

// A synthetic address, so the met leg stores a genuinely present value.
const STORED_ADDRESS = "http://config-guard.invalid:6969";
// The unset state the guard exists to refuse.
const NO_ADDRESS = "";

const REASON_SETTING = /Set the Whisparr URL in Whisparr Sync settings \(Connection\)/;
const REASON_CONSEQUENCE = /every Whisparr action fails as though Whisparr were not running/;

// What a dimmed control carries instead of the whole sentence: the setting and its section, and no more. The
// literal is pinned by the offline gate; here it composes the accessible names these cases locate controls by.
const REASON_REQUIREMENT = "Needs the Whisparr URL setting (Connection)";
const REQUIREMENT_ANYWHERE = /Needs the Whisparr URL setting \(Connection\)/;
/** A refused control's whole accessible name: its own name first, the reason behind one separator. */
const refusedName = (own) => `${own} — ${REASON_REQUIREMENT}`;

/**
 * Writes the stored options with a COMPLETE connection object, varying only the address. Every field is sent
 * because `WithSubmitted` rebuilds the saved connection on write, so a partial body is how a saved connection
 * gets lost. The address is a synthetic host — nothing in this spec reaches Whisparr.
 */
async function storeAddress(api, baseUrl) {
  const res = await api.post(`/api/extensions/${EXTENSION_ID}/options`, {
    BaseUrl: baseUrl,
    ApiKey: "config-guard-spec-key",
    SelectedVersion: "v3",
    TagsOnAdd: ["cove"],
    MonitorNewByDefault: true,
    AllowQualityUpgrades: false,
  });
  expect(res.status, `storing the options succeeded (body: ${res.text})`).toBeLessThan(300);

  // Read the server's own answer back rather than trusting the write: this is the fact every guarded control
  // consults, so a case that assumed it would be asserting against the wrong premise.
  const status = await api.get(`/api/extensions/${EXTENSION_ID}/status`);
  expect(status.status).toBe(200);
  expect(status.json.missingRequiredOptions).toEqual(baseUrl === NO_ADDRESS ? ["baseUrl"] : []);
}

/** Asserts the refusal sentence reached the accessibility tree as a rendered node, carrying its consequence. */
async function expectRenderedReason(scope) {
  const reason = scope.getByRole("status").filter({ hasText: REASON_SETTING });
  await expect(reason.first()).toBeVisible();
  expect(await reason.first().innerText()).toMatch(REASON_CONSEQUENCE);
}

/** Asserts no refusal sentence is rendered anywhere in the scope. */
async function expectNoRenderedReason(scope) {
  await expect(scope.getByText(REASON_SETTING)).toHaveCount(0);
}

/** Asserts the full sentence occupies exactly `n` status regions in the scope — a count, never a presence check. */
async function expectSentenceCount(scope, n) {
  await expect(scope.getByRole("status").filter({ hasText: REASON_SETTING })).toHaveCount(n);
}

/** Asserts a refused control keeps its own name at the front of the name a screen reader reads out. */
async function expectNameStartsWithOwnName(locator, own) {
  await expect(locator).toHaveAccessibleName(refusedName(own));
  await expect(locator).toBeDisabled();
}

/**
 * The same property for a control whose own label is state-dependent: the reason is a suffix behind one
 * separator and something that is not the reason comes first. Pinning the label here would tie the case to a
 * fixture's monitored state rather than to the accessibility rule.
 */
async function expectComposedName(locator) {
  await expect(locator).toBeDisabled();
  const name = await locator.getAttribute("aria-label");
  expect(name).not.toBeNull();
  expect(name.endsWith(` — ${REASON_REQUIREMENT}`)).toBe(true);
  expect(name.startsWith(REASON_REQUIREMENT)).toBe(false);
}

/** Asserts no title attribute in the scope carries the whole sentence — a tooltip carries the short reason. */
async function expectNoSentenceInTitles(scope) {
  const titles = await scope
    .locator("[title]")
    .evaluateAll((els) => els.map((e) => e.getAttribute("title") ?? ""));
  expect(titles.filter((t) => REASON_SETTING.test(t))).toEqual([]);
}

function syntheticScenes(n) {
  const base = Date.UTC(2018, 0, 1);
  const scenes = [];
  for (let i = 1; i <= n; i++) {
    const pad = String(i).padStart(4, "0");
    scenes.push({
      sourceId: `synthetic-${pad}`,
      title: `Scene ${pad}`,
      releaseDate: new Date(base + i * 86_400_000).toISOString().slice(0, 10),
      entityName: "Synthetic Studio",
      studioName: "Synthetic Studio",
      posterUrl: null,
      coverUrl: `https://cdn.example.test/cover/${pad}.jpg`,
    });
  }
  return scenes;
}

/** A monitored, add-capable v3 entity — the state in which the menu offers both guarded bulk items. */
async function routeMonitorStatus(page) {
  await page.route("**/monitor-status", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        added: true,
        monitored: true,
        scenesPresent: 1,
        scenesTotal: 2,
        hasCounts: true,
        addSupported: true,
        ownedImportSupported: true,
      }),
    }),
  );
}

/** A not-yet-added v3 scene — the state in which the panel offers Add plus the ON leg of the monitor toggle. */
async function routeSceneDetail(page) {
  await page.route("**/scene-detail", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        state: "notAdded",
        added: false,
        monitored: false,
        hasFile: false,
        quality: null,
        cutoffMet: null,
        actionsSupported: true,
      }),
    }),
  );
}

async function routeDiscovery(page, scenes) {
  await page.route("**/discovery/entity", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        scenes,
        entityName: "Synthetic Studio",
        state: "ok",
        source: "whisparr",
        version: "v3",
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

async function firstStudioId({ harness, baseUrl }) {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId, "seedCorpus should link at least one scene to a studio").not.toBeUndefined();
  return studioId;
}

// The entity surface produces a MIXED menu — the guard dims two items and leaves the rest live — so the
// refusal belongs on those two items and NOT on the action-row button that opens the menu. Disabling that button
// would be the only door to a mixed room, taking away two verbs the guard does not dim over an unrelated
// setting. The trigger's own disabled states are reserved for causes that make every item unusable (no identity,
// an unsupported entity, an unreachable instance), which is why this case asserts the trigger ENABLED in both
// states — that assertion is what stops the over-refusal coming back.
test("the entity menu dims only the two create items, keeps its other verbs live, and never closes its own door", async ({
  harness,
  baseUrl,
  page,
  api,
}, testInfo) => {
  const studioId = await firstStudioId({ harness, baseUrl });
  await routeMonitorStatus(page);
  const detail = new EntityDetailPage(page, baseUrl);
  const menu = detail.monitorMenu;
  const monitorItem = () => menu.getByRole("menuitemcheckbox");
  const addAllMissing = () => menu.getByRole("menuitem", { name: /Add all missing/ });
  const reflectOwned = () => menu.getByRole("menuitem", { name: /Reflect owned in Whisparr/ });
  const searchMonitored = () => menu.getByRole("menuitem", { name: /Search all monitored/ });

  // ---- (1) an unset stored address: the two create items refuse, everything else stays live ----
  await storeAddress(api, NO_ADDRESS);
  await detail.gotoStudio(studioId);

  const triggerPresent = await detail.monitorMenuTrigger
    .waitFor({ state: "visible", timeout: 15_000 })
    .then(() => true)
    .catch(() => false);
  test.skip(!triggerPresent, "this Cove build did not render the studio action-row slot at all");

  // The door stays open — an unset setting is not a reason to withhold the whole menu.
  await expect(detail.monitorMenuTrigger).toBeEnabled();
  await detail.openMonitorMenu();

  // Each refused item names ITSELF first and carries the short requirement behind one separator, so it is
  // located by its own label. Exactly two menu items are refused; the count is the assertion.
  await expectComposedName(monitorItem());
  await expectNameStartsWithOwnName(addAllMissing(), "Add all missing");
  // The monitor toggle is a menuitemcheckbox, so the refused set is the union of the two roles.
  const refused = menu
    .getByRole("menuitem", { name: REQUIREMENT_ANYWHERE })
    .or(menu.getByRole("menuitemcheckbox", { name: REQUIREMENT_ANYWHERE }));
  await expect(refused).toHaveCount(2);
  await expectRenderedReason(menu);
  // The connection-global fact belongs to the popover once, and to no tooltip inside it.
  await expectSentenceCount(menu, 1);
  await expectNoSentenceInTitles(menu);
  // The pairing, and the whole point: the verbs the guard does not dim are reachable AND live.
  await expect(reflectOwned()).toBeEnabled();
  await expect(searchMonitored()).toBeEnabled();
  await page.screenshot({ path: testInfo.outputPath("entity-menu-refused.png") });
  await page.keyboard.press("Escape");

  // ---- (2) a stored address: the same two items are enabled and no reason is rendered ----
  await storeAddress(api, STORED_ADDRESS);
  await detail.gotoStudio(studioId);
  await expect(detail.monitorMenuTrigger).toBeEnabled();
  await detail.openMonitorMenu();

  await expect(monitorItem()).toBeEnabled();
  await expect(addAllMissing()).toBeEnabled();
  await expect(reflectOwned()).toBeEnabled();
  await expect(searchMonitored()).toBeEnabled();
  await expect(menu.getByRole("menuitemradio").first()).toBeEnabled();
  await expectNoRenderedReason(menu);
  await expectSentenceCount(menu, 0);
  await expect(
    menu
      .getByRole("menuitem", { name: REQUIREMENT_ANYWHERE })
      .or(menu.getByRole("menuitemcheckbox", { name: REQUIREMENT_ANYWHERE })),
  ).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("entity-menu-enabled.png") });
});

test("the scene panel dims Add and the monitor ON leg, and keeps the exclusion control live", async ({
  harness,
  baseUrl,
  page,
  api,
}, testInfo) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const videoId = [...seeded.values()].map((s) => s.coveVideoId).find((id) => id != null);
  expect(videoId, "seedCorpus should register at least one video").not.toBeUndefined();

  await routeSceneDetail(page);
  const panel = new ScenePanel(page, baseUrl);

  // ---- (1) an unset stored address ----
  await storeAddress(api, NO_ADDRESS);
  await panel.gotoVideo(videoId);

  const tabPresent = await panel.whisparrTab
    .waitFor({ state: "visible", timeout: 10_000 })
    .then(() => true)
    .catch(() => false);
  test.skip(!tabPresent, "this Cove build did not render the extension's video detail-rail tab");
  await panel.openWhisparrTab();

  const refused = page.getByRole("button", { name: REQUIREMENT_ANYWHERE });
  await expect(refused.first()).toBeVisible();
  // Both guarded controls carry the short requirement behind their own name, so both are matched by it.
  await expect.poll(() => refused.count()).toBeGreaterThanOrEqual(2);
  for (const control of await refused.all()) {
    await expect(control).toBeDisabled();
  }
  await expectNameStartsWithOwnName(
    page.getByRole("button", { name: refusedName("Add to Whisparr") }),
    "Add to Whisparr",
  );
  await expectNameStartsWithOwnName(
    page.getByRole("button", { name: refusedName("Monitor this scene") }),
    "Monitor this scene",
  );
  await expectRenderedReason(page);
  await expectSentenceCount(page, 1);
  await expectNoSentenceInTitles(page);
  // The exclusion control is not one the guard dims, and its route is not refused here either.
  await expect(page.getByRole("button", { name: /Exclude from Whisparr/ })).toBeEnabled();
  await page.screenshot({ path: testInfo.outputPath("scene-panel-refused.png") });

  // ---- (2) a stored address: the two named controls are back, enabled, under their own labels ----
  await storeAddress(api, STORED_ADDRESS);
  await panel.gotoVideo(videoId);
  await panel.openWhisparrTab();

  // The button names itself; "Add this scene to Whisparr" is its hover text, not its accessible name.
  await expect(page.getByRole("button", { name: "Add to Whisparr" })).toBeEnabled();
  await expect(page.getByRole("button", { name: "Monitor this scene" })).toBeEnabled();
  await expect(page.getByRole("button", { name: /Exclude from Whisparr/ })).toBeEnabled();
  await expectNoRenderedReason(page);
  await expectSentenceCount(page, 0);
  await page.screenshot({ path: testInfo.outputPath("scene-panel-enabled.png") });
});

// The list is long on purpose. A count of one on a four-card page would have passed before this rollout as
// easily as after it; only a page with many cards can show that the sentence stopped following the item count.
test("a missing-scene card dims Mark wanted, keeps Search live, and the tab states the reason once", async ({
  harness,
  baseUrl,
  page,
  api,
}, testInfo) => {
  const studioId = await firstStudioId({ harness, baseUrl });
  const scenes = syntheticScenes(24);
  await routeDiscovery(page, scenes);

  async function openMissingTab() {
    const detail = new EntityDetailPage(page, baseUrl);
    await detail.gotoStudio(studioId);
    const missingTab = page.getByRole("tab", { name: /Missing/ });
    await expect(missingTab).toBeVisible();
    await missingTab.click();
    await expect(missingTab).toHaveAttribute("aria-selected", "true");
  }

  // ---- (1) an unset stored address: Mark wanted refuses under the reason as its accessible name ----
  await storeAddress(api, NO_ADDRESS);
  await openMissingTab();

  const refusedMonitor = page.getByRole("button", { name: refusedName("Mark this scene wanted") });
  await expect.poll(() => refusedMonitor.count()).toBe(scenes.length);
  await expect(refusedMonitor.first()).toBeDisabled();
  // The unrefused label is gone precisely because the reason is appended to it — assert the absence too, so a
  // future change that leaves the label AND disables the button cannot pass this case silently.
  await expect(
    page.getByRole("button", { name: "Mark this scene wanted", exact: true }),
  ).toHaveCount(0);
  await expectRenderedReason(page);
  // Twenty-four dimmed cards, ONE statement, and it is on none of them.
  await expectSentenceCount(page, 1);
  await expectNoSentenceInTitles(page);
  // Search is not a control the guard dims, so it must stay live here.
  await expect(page.getByRole("button", { name: /^Search for Scene 0001 now$/ })).toBeEnabled();
  await page.screenshot({ path: testInfo.outputPath("card-refused.png") });

  // ---- (2) a stored address: every card's Mark wanted is back under its own label, enabled ----
  await storeAddress(api, STORED_ADDRESS);
  await openMissingTab();

  const liveMonitor = page.getByRole("button", { name: "Mark this scene wanted" });
  await expect.poll(() => liveMonitor.count()).toBe(scenes.length);
  await expect(liveMonitor.first()).toBeEnabled();
  await expect(page.getByRole("button", { name: /^Search for Scene 0001 now$/ })).toBeEnabled();
  await expectNoRenderedReason(page);
  await expectSentenceCount(page, 0);
  await page.screenshot({ path: testInfo.outputPath("card-enabled.png") });
});

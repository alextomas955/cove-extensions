// Drives the reshaped MissingSceneCard's BEHAVIOR through the running host: the Missing-tab card grid renders the
// native-skeleton card (cover + title + date/studio meta + inline performer/tag chip rows + the always-on
// three-state status pill), and clicking a card's Monitor flips its status pill to Wanted. Visual parity with the
// native videos card is structural (the card reuses Cove's global video-card/card-media/card-body/card-title
// classes), so it is NOT asserted by measuring pixels here — a human screenshot glance covers the eyeball check.
// The pure diff / status classification / chip formatting live in the offline *Logic.ts unit gates and are not
// re-tested here (test pyramid: unit for logic, e2e for the real click-through).
//
// Two tiers:
//   1. Hermetic (always runs via the containerized harness): route-intercepts the discovery reads with a small
//      SYNTHETIC list (some rows carrying performers/tags, one carrying only studio/date, each a distinct
//      status) and drives the render + a Monitor click.
//   2. Live cove-dev click-through (runs only when COVE_DEV_URL is set): authenticates, opens a real studio
//      Missing tab fed a synthetic list (content-safe — no real scene metadata surfaces), screenshots the grid
//      next to the native videos page for a human glance, and confirms a Monitor click flips the pill to Wanted.
import {
  test,
  expect,
  seedCorpus,
  routeUsableConfiguration,
} from "../lib/whisparrsync-fixtures.mjs";
import { test as base, expect as baseExpect } from "@cove-extensions/e2e";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

// A synthetic missing list: one row carries many performers/tags (so a chip strip + a +N cap renders), one
// carries only studio/date (chip rows omitted), each a distinct status. Numeric/neutral labels only (SFW).
function syntheticScenes() {
  const p = (name, imageUrl) => ({ name, imageUrl });
  return [
    {
      sourceId: "synthetic-0001",
      title: "Scene 0001",
      releaseDate: "2018-01-02",
      entityName: "Synthetic Studio",
      studioName: "Synthetic Studio",
      posterUrl: null,
      coverUrl: "https://cdn.example.test/cover/0001.jpg",
      performers: [
        p("Performer One", "https://cdn.example.test/performer/1.jpg"),
        p("Performer Two", null),
        p("Performer Three", null),
        p("Performer Four", null),
        p("Performer Five", null),
      ],
      tags: ["Tag A", "Tag B", "Tag C", "Tag D", "Tag E", "Tag F", "Tag G"],
      overview:
        "A synthetic scene blurb rendered as the card's two-line description.",
      status: "wanted",
    },
    {
      sourceId: "synthetic-0002",
      title: "Scene 0002",
      releaseDate: "2018-02-02",
      entityName: "Synthetic Studio",
      studioName: "Synthetic Studio",
      posterUrl: null,
      coverUrl: "https://cdn.example.test/cover/0002.jpg",
      performers: [],
      tags: [],
      overview: null,
      status: "notAdded",
    },
    {
      sourceId: "synthetic-0003",
      title: "Scene 0003",
      releaseDate: "2018-03-02",
      entityName: "Synthetic Studio",
      studioName: "Synthetic Studio",
      posterUrl: null,
      coverUrl: "https://cdn.example.test/cover/0003.jpg",
      performers: [p("Solo Performer", null)],
      tags: ["Single Tag"],
      overview: null,
      status: "unmonitored",
    },
  ];
}

async function routeDiscovery(page, scenes, actionPosts) {
  // The per-scene verbs below are configuration-guarded; without this the spec inherits the ambient
  // instance's quality profile and dims the very controls it came to click.
  await routeUsableConfiguration(page);

  await page.route("**/discovery/entity", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        scenes,
        entityName: "Synthetic Studio",
        state: "ok",
        source: "stashdb",
        version: "v3",
      }),
    });
  });
  await page.route("**/discovery/count**", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ count: scenes.length }),
    });
  });
  await page.route("**/discovery/action", async (route) => {
    actionPosts.push(route.request().postDataJSON());
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({ total: 1, succeeded: 1, failed: 0 }),
    });
  });
}

async function openStudioMissingTab(page, detail, studioId) {
  await detail.gotoStudio(studioId);
  const missingTab = page.getByRole("tab", { name: /Missing/ });
  await expect(missingTab).toBeVisible();
  await missingTab.click();
  await expect(missingTab).toHaveAttribute("aria-selected", "true");
}

test("missing card renders the reshaped skeleton + chips + status pill, and Monitor flips the pill to Wanted", async ({
  harness,
  baseUrl,
  page,
}, testInfo) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()]
    .map((s) => s.studioId)
    .find((id) => id != null);
  expect(
    studioId,
    "seedCorpus should link at least one scene to a studio",
  ).not.toBeUndefined();

  const actionPosts = [];
  await routeDiscovery(page, syntheticScenes(), actionPosts);
  const detail = new EntityDetailPage(page, baseUrl);
  await openStudioMissingTab(page, detail, studioId);

  // The grid renders one card per scene, scoped under the tab's list region so host cards elsewhere never leak.
  const cards = page
    .getByRole("list", { name: "Missing scenes" })
    .locator('[role="listitem"] .video-card');
  await expect.poll(() => cards.count()).toBe(3);

  // The native class skeleton is present on the card (structure the card inherits by reusing the host classes).
  const card1 = page.locator(".video-card", { hasText: "Scene 0001" });
  await expect(card1.locator(".card-media")).toBeVisible();
  await expect(card1.locator(".card-body")).toBeVisible();
  await expect(card1.locator(".card-title")).toHaveText("Scene 0001");

  // The meta row renders date + studio; the chip-carrying card shows inline performer chips (avatar/name) with a
  // +N cap (5 performers → 4 shown + "+1"), a two-line description from the overview, and a footer count row.
  await expect(card1.getByText("2018-01-02")).toBeVisible();
  await expect(card1.getByText("Performer One")).toBeVisible();
  await expect(card1.getByText("+1")).toBeVisible();
  await expect(card1.getByText(/A synthetic scene blurb/)).toBeVisible();
  // The first performer carries an avatar image; the chip renders it as an <img> (not the placeholder glyph).
  await expect
    .poll(() => card1.locator("img[loading='lazy']").count())
    .toBeGreaterThan(1);

  // The metadata-only card omits the performer chips, the description, and the counts footer entirely (no
  // rounded-full chips / avatar), never an empty strip.
  const card2 = page.locator(".video-card", { hasText: "Scene 0002" });
  await expect.poll(() => card2.locator(".rounded-full").count()).toBe(0);

  // The always-on three-state status pill renders on every card (no show-Whisparr-status toggle gates it).
  await expect(card1.getByText("Wanted")).toBeVisible();
  await expect(card2.getByText("Not added")).toBeVisible();
  const card3 = page.locator(".video-card", { hasText: "Scene 0003" });
  await expect(card3.getByText("Unmonitored")).toBeVisible();

  await page.screenshot({ path: testInfo.outputPath("missing-card-grid.png") });

  // ---- Behavior: clicking Monitor on the "Not added" card flips its status pill to Wanted ----
  await card2.getByRole("button", { name: "Mark this scene wanted" }).click();
  await expect.poll(() => actionPosts.length).toBe(1);
  expect(actionPosts[0]).toMatchObject({
    Op: "monitor",
    Kind: "studio",
    CoveEntityId: studioId,
  });
  // The pill flips notAdded → Wanted and the per-card Monitor becomes its confirmed state — the card now offers
  // Unmonitor (un-mark) and shows the "Wanted" status pill, with "Not added" gone.
  await expect(card2.getByText("Not added")).toHaveCount(0);
  await expect(
    card2.getByRole("button", {
      name: "Remove Scene 0002 from the wanted list",
    }),
  ).toBeVisible();
  await expect(card2.getByText("Wanted")).toBeVisible();
});

test("card selection toggle is hover-reveal (hidden until hover, solid when selected/selecting), and the count label shows the catalogue total", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()]
    .map((s) => s.studioId)
    .find((id) => id != null);
  expect(
    studioId,
    "seedCorpus should link at least one scene to a studio",
  ).not.toBeUndefined();

  const actionPosts = [];
  await routeDiscovery(page, syntheticScenes(), actionPosts);
  const detail = new EntityDetailPage(page, baseUrl);
  await openStudioMissingTab(page, detail, studioId);

  const card2 = page.locator(".video-card", { hasText: "Scene 0002" });
  await expect(card2).toBeVisible();
  // The card root is a Tailwind `group` so the toggle's group-hover reveal fires.
  await expect
    .poll(async () => (await card2.getAttribute("class")) ?? "")
    .toContain("group");

  // Before hover/selection the toggle is opacity-gated (hidden until the card is hovered), not display-hidden.
  const toggle2 = page.getByRole("button", { name: "Select Scene 0002" });
  const idleClass = (await toggle2.getAttribute("class")) ?? "";
  expect(idleClass).toContain("opacity-0");
  expect(idleClass).toContain("group-hover:opacity-100");

  // Hovering the card reveals the toggle — its computed opacity resolves to 1 (behavior, not a pixel assertion).
  await card2.hover();
  await expect
    .poll(() => toggle2.evaluate((el) => getComputedStyle(el).opacity))
    .toBe("1");

  // Selecting the card makes its toggle solid (opacity-100) and flips it to the pressed "Deselect" state.
  await toggle2.click();
  const deselect2 = page.getByRole("button", { name: "Deselect Scene 0002" });
  await expect(deselect2).toBeVisible();
  await expect(deselect2).toHaveAttribute("aria-pressed", "true");
  await expect
    .poll(async () => (await deselect2.getAttribute("class")) ?? "")
    .toContain("opacity-100");

  // With a selection in progress every OTHER card's toggle goes solid too (selecting mode), no hover required.
  const toggle1 = page.getByRole("button", { name: "Select Scene 0001" });
  await expect
    .poll(async () => (await toggle1.getAttribute("class")) ?? "")
    .toContain("opacity-100");

  // The count label reads the catalogue total in Cove's "start-end of total" convention (3 synthetic scenes),
  // not a per-page row count.
  await expect(page.getByText(/1[-–]3 of 3/)).toBeVisible();
});

// ---- Live cove-dev click-through + human-glance screenshots (env-gated: COVE_DEV_URL) ----
// Uses only the base `page` fixture (no containerized harness). Content-safe: the Missing tab is fed a synthetic
// list via a browser route-intercept, so no real scene metadata surfaces and no real Whisparr mutation occurs.

const COVE_DEV_URL = process.env.COVE_DEV_URL;
const COVE_DEV_USER = process.env.COVE_DEV_USER ?? "dev-owner";
const COVE_DEV_PASSWORD = process.env.COVE_DEV_PASSWORD ?? "DevPassword123!";

base.describe("missing card — live cove-dev click-through", () => {
  base.skip(
    !COVE_DEV_URL,
    "COVE_DEV_URL not set — the live cove-dev click-through is opt-in.",
  );

  base(
    "renders under the real host stylesheet and a Monitor click flips the pill to Wanted",
    async ({ page }, testInfo) => {
      // Authenticate through the SPA's sign-in form (it submits POST /api/auth/login, which also sets the session
      // cookie the extension's cookie-based request() reads). The form is the login wall shown to an unauthenticated
      // browser; fill it and wait for it to clear.
      await page.goto(`${COVE_DEV_URL}/`, { waitUntil: "networkidle" });
      const usernameField = page.locator("#login-username");
      await usernameField.waitFor({ state: "visible", timeout: 15_000 });
      await usernameField.fill(COVE_DEV_USER);
      await page.locator("#login-password").fill(COVE_DEV_PASSWORD);
      await page.locator("button", { hasText: "Sign in" }).click();
      await usernameField.waitFor({ state: "detached", timeout: 20_000 });

      // A human-glance screenshot of the native videos grid — the reference the reshaped card is eyeballed against.
      await page.goto(`${COVE_DEV_URL}/videos`, { waitUntil: "networkidle" });
      await page
        .locator(".video-card")
        .first()
        .waitFor({ state: "visible", timeout: 20_000 });
      await page.screenshot({
        path: testInfo.outputPath("native-videos-grid.png"),
      });

      // Feed a studio's Missing tab a synthetic list (content-safe) so the reshaped card renders under the REAL
      // host stylesheet with no real metadata and no real Whisparr call.
      const actionPosts = [];
      await routeDiscovery(page, syntheticScenes(), actionPosts);
      await page.goto(`${COVE_DEV_URL}/studios`, { waitUntil: "networkidle" });
      const studioHref = await page
        .locator('a[href^="/studio/"]')
        .first()
        .getAttribute("href");
      baseExpect(
        studioHref,
        "cove-dev should list at least one studio",
      ).toBeTruthy();
      await page.goto(`${COVE_DEV_URL}${studioHref}`, {
        waitUntil: "networkidle",
      });
      const missingTab = page.getByRole("tab", { name: /Missing/ });
      await missingTab.waitFor({ state: "visible", timeout: 20_000 });
      await missingTab.click();

      const card = page.locator('[role="listitem"] .video-card', {
        hasText: "Scene 0002",
      });
      await card.waitFor({ state: "visible", timeout: 20_000 });
      // A human-glance screenshot of the reshaped Missing-tab grid under the real host stylesheet — scroll the grid
      // into view first so the cards themselves (not just the tab header) are captured for the eyeball comparison.
      await card.scrollIntoViewIfNeeded();
      await page
        .locator('[role="listitem"] .video-card')
        .first()
        .scrollIntoViewIfNeeded();
      await page.screenshot({
        path: testInfo.outputPath("missing-tab-grid.png"),
      });

      // Behavior: a Monitor click flips the pill to Wanted (the reshaped card's status pill wiring, in the real host).
      await baseExpect(card.getByText("Not added")).toBeVisible();
      await card
        .getByRole("button", { name: "Mark this scene wanted" })
        .click();
      await baseExpect.poll(() => actionPosts.length).toBe(1);
      // The card flips to its confirmed state: Unmonitor (un-mark) is offered and the "Wanted" pill shows; "Not
      // added" is gone.
      await baseExpect(card.getByText("Not added")).toHaveCount(0);
      await baseExpect(
        card.getByRole("button", {
          name: "Remove Scene 0002 from the wanted list",
        }),
      ).toBeVisible();
      await baseExpect(card.getByText("Wanted")).toBeVisible();
    },
  );
});

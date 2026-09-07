// Drives a PARENT studio's Missing tab end-to-end through the running host, proving the aggregation behavior:
// the tab renders cards spanning more than one child sub-studio (the parent's catalogue unions its children),
// the parent-only sub-studio facet control is offered, and selecting one child narrows the visible cards to
// that single sub-studio. Behavior only — it asserts on the studio labels the cards carry and on which cards
// remain, never on card size/CSS (structural parity is guaranteed by the shared card classes elsewhere).
//
// The hermetic harness stands up no Whisparr, so this route-intercepts /discovery/entity with a small SYNTHETIC
// list (numeric titles, content-safe placeholder sub-studios/performers/tags — no real metadata) carrying
// isParent:true so the sub-studio facet renders and the narrowing is deterministic.
import { test, expect, seedCorpus } from "../lib/whisparrsync-fixtures.mjs";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

async function routeParentDiscovery(page, scenes) {
  await page.route("**/discovery/entity", async (route) => {
    await route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        scenes,
        entityName: "Parent Network",
        state: "ok",
        source: "stashdb",
        version: "v3",
        isParent: true,
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
}

async function openStudioMissing(page, baseUrl, studioId) {
  const detail = new EntityDetailPage(page, baseUrl);
  await detail.gotoStudio(studioId);
  const missingTab = page.getByRole("tab", { name: /Missing/ });
  await expect(missingTab).toBeVisible();
  await missingTab.click();
  await expect(missingTab).toHaveAttribute("aria-selected", "true");
}

// Every visible card carries one hover-reveal "Select <title>" toggle button, so their count is the visible-card
// count (the toggle is opacity-gated, not display-hidden, so it is still in the accessibility tree to count).
// SCOPED to the missing list: Cove's own entity cards carry a "Select item" button of their own, so an unscoped
// match counts host cards whenever this tab has not mounted yet.
function cardCount(page) {
  return page
    .getByRole("list", { name: "Missing scenes" })
    .getByRole("button", { name: /^Select / })
    .count();
}

// A card whose meta row carries the given sub-studio name.
function cardsForStudio(page, name) {
  return page.locator(".video-card", { hasText: name });
}

test("parent studio Missing tab spans child sub-studios and the sub-studio facet narrows to one", async ({
  harness,
  baseUrl,
  page,
}) => {
  const seeded = await seedCorpus({ container: harness.container, baseUrl });
  const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
  expect(studioId, "seedCorpus should link at least one scene to a studio").not.toBeUndefined();

  // A parent's aggregated catalogue: three scenes across TWO child sub-studios (two under one child, one under
  // the other), each row carrying its own child studio name.
  const scenes = [
    { sourceId: "sub-1", title: "Scene 0001", studioName: "Child Studio One", performers: [{ name: "Synthetic Performer" }], tags: ["Tag Red"], releaseDate: "2023-01-01", entityName: "Parent Network", posterUrl: null, status: "notAdded" },
    { sourceId: "sub-2", title: "Scene 0002", studioName: "Child Studio One", performers: [{ name: "Synthetic Performer" }], tags: ["Tag Blue"], releaseDate: "2022-01-01", entityName: "Parent Network", posterUrl: null, status: "notAdded" },
    { sourceId: "sub-3", title: "Scene 0003", studioName: "Child Studio Two", performers: [{ name: "Synthetic Performer" }], tags: ["Tag Red"], releaseDate: "2021-01-01", entityName: "Parent Network", posterUrl: null, status: "notAdded" },
  ];
  await routeParentDiscovery(page, scenes);

  await openStudioMissing(page, baseUrl, studioId);
  await expect.poll(() => cardCount(page)).toBe(3);

  // The union spans children: cards for BOTH child sub-studios render (assert on the studio labels the cards carry).
  await expect(cardsForStudio(page, "Child Studio One").first()).toBeVisible();
  await expect(cardsForStudio(page, "Child Studio Two").first()).toBeVisible();

  // A parent studio is the one studio page that offers the sub-studio facet (a non-parent studio never does).
  const subStudioFacet = page.getByRole("combobox", { name: "Filter by sub-studio" });
  await expect(subStudioFacet).toHaveCount(1);

  // The sub-studio facet is a searchable combobox: open it and pick the child by its option label.
  // Selecting one child narrows the visible cards to that single sub-studio — the other child's cards drop out.
  await subStudioFacet.click();
  await page
    .getByRole("listbox", { name: "Filter by sub-studio" })
    .getByRole("option", { name: "Child Studio One", exact: true })
    .click();
  await expect.poll(() => cardCount(page)).toBe(2);
  await expect.poll(() => cardsForStudio(page, "Child Studio Two").count()).toBe(0);
  await expect(cardsForStudio(page, "Child Studio One").first()).toBeVisible();
});

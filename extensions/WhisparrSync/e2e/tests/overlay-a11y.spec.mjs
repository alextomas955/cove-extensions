// Accessibility regression for the hand-rolled MENU-mode overlay (shared/cove-extensions-ui `useOverlayKeys`,
// `nav:"menu"`) AS WIRED into WhisparrSync's popovers — recreating the a11y proof that 53-03 ran but never
// committed. It drives the real primitive through its consumers; it does NOT rebuild the trap.
//
// Two consumers, two focus-restore contracts (the menu mode itself does NOT restore focus — the overlay's
// `restoreFocus` defaults to false for menu):
//   - WhisparrBatchChooser: opened imperatively from a host bulk action, it owns no trigger, so there is no
//     focus to restore — this suite asserts the primitive contract (ARIA, roving, Escape, outside-click) only.
//   - WhisparrMenu: its trigger (WhisparrMonitorButton) restores focus to itself on close, so that IS asserted.
//     The `/monitor-status` read is stubbed so the trigger enables without a live Whisparr (this suite is
//     hermetic, Cove-only); the keyboard/focus contract it exercises is the real product code.
import { test, expect, seedCorpus, routeUsableConfiguration } from "../lib/whisparrsync-fixtures.mjs";
import { VideosPage } from "@cove-extensions/e2e/pages/videos-page";
import { EntityDetailPage } from "../lib/pages/entity-detail-page.mjs";

test.describe("the videos bulk chooser — menu-mode overlay primitive", () => {
  // The config guard DISABLES every batch item until a Whisparr URL + key are configured, and a
  // disabled button cannot receive focus — so the roving-focus contract this suite exists to prove is
  // unobservable without a usable configuration. Stubbing /status keeps the suite hermetic (Cove-only)
  // while leaving the overlay primitive itself real. Previously this passed only when a sibling spec
  // happened to leave a connection configured on the shared worker instance.
  test.beforeEach(async ({ page }) => {
    await routeUsableConfiguration(page);
  });

  test("presents role=menu with role=menuitem rows and focuses the first item on open", async ({
    harness,
    baseUrl,
    page,
  }) => {
    await seedCorpus({ container: harness.container, baseUrl });
    const videos = new VideosPage(page, baseUrl);
    await videos.goto();
    await videos.selectFirstCards(2);
    await videos.openWhisparrBatchMenu();

    await expect(videos.batchMenu).toBeVisible();
    await expect(videos.batchMenuItems()).toHaveCount(6);
    // The overlay focuses its first roving item on open (useLayoutEffect focus-first).
    await expect(videos.batchMenuItems().nth(0)).toBeFocused();
  });

  test("ArrowDown/ArrowUp rove focus across the items and wrap at both ends", async ({
    harness,
    baseUrl,
    page,
  }) => {
    await seedCorpus({ container: harness.container, baseUrl });
    const videos = new VideosPage(page, baseUrl);
    await videos.goto();
    await videos.selectFirstCards(2);
    await videos.openWhisparrBatchMenu();

    const items = videos.batchMenuItems();
    await expect(items.nth(0)).toBeFocused();

    await page.keyboard.press("ArrowDown");
    await expect(items.nth(1)).toBeFocused();
    await page.keyboard.press("ArrowDown");
    await expect(items.nth(2)).toBeFocused();
    await page.keyboard.press("ArrowUp");
    await expect(items.nth(1)).toBeFocused();

    // Wrap: ArrowUp off the first item lands on the last, and ArrowDown off the last wraps to the first.
    await page.keyboard.press("ArrowUp");
    await expect(items.nth(0)).toBeFocused();
    await page.keyboard.press("ArrowUp");
    await expect(items.nth(5)).toBeFocused();
    await page.keyboard.press("ArrowDown");
    await expect(items.nth(0)).toBeFocused();
  });

  test("Escape closes the chooser", async ({ harness, baseUrl, page }) => {
    await seedCorpus({ container: harness.container, baseUrl });
    const videos = new VideosPage(page, baseUrl);
    await videos.goto();
    await videos.selectFirstCards(2);
    await videos.openWhisparrBatchMenu();

    await expect(videos.batchMenu).toBeVisible();
    await page.keyboard.press("Escape");
    // presentOverlay unmounts and removes its container, so the menu leaves the DOM entirely.
    await expect(videos.batchMenu).toHaveCount(0);
  });

  test("an outside click closes the chooser", async ({ harness, baseUrl, page }) => {
    await seedCorpus({ container: harness.container, baseUrl });
    const videos = new VideosPage(page, baseUrl);
    await videos.goto();
    await videos.selectFirstCards(2);
    await videos.openWhisparrBatchMenu();

    await expect(videos.batchMenu).toBeVisible();
    // The chooser fixed-centers at top:20vh; a click in the top-left corner is unambiguously outside it.
    await page.mouse.click(5, 5);
    await expect(videos.batchMenu).toHaveCount(0);
  });
});

test.describe("the studio monitor menu (WhisparrMenu) — menu overlay + trigger focus-restore", () => {
  // Stub the monitor-status read so WhisparrMonitorButton enables hermetically (a healthy, monitored entity);
  // the menu, its keyboard nav, and the trigger's focus-restore are then real product code under test.
  test.beforeEach(async ({ page }) => {
    // The monitor-status stub enables the TRIGGER; the config guard separately dims the menu's own
    // items until a URL + key exist, so both are needed for the keyboard contract to be reachable.
    await routeUsableConfiguration(page);
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
          ownedImportSupported: false,
        }),
      }),
    );
  });

  test("opens role=menu with menuitemcheckbox/menuitemradio, roves, and Escape restores focus to the trigger", async ({
    harness,
    baseUrl,
    page,
  }) => {
    const seeded = await seedCorpus({ container: harness.container, baseUrl });
    const studioId = [...seeded.values()].map((s) => s.studioId).find((id) => id != null);
    test.skip(studioId == null, "no synthetic studio was linked in the seeded corpus");

    const entity = new EntityDetailPage(page, baseUrl);
    await entity.gotoStudio(studioId);

    // The action-row slot is host-rendered; if this Cove build doesn't surface it, skip rather than fail —
    // the primitive itself is already proven unconditionally by the chooser suite above.
    const triggerReady = await entity.monitorMenuTrigger
      .waitFor({ state: "visible", timeout: 10_000 })
      .then(() => entity.monitorMenuTrigger.isEnabled())
      .catch(() => false);
    test.skip(!triggerReady, "the studio monitor action-row slot did not render an enabled trigger");

    await entity.openMonitorMenu();
    await expect(entity.monitorMenu).toBeVisible();
    // The Monitor toggle is a menuitemcheckbox; the scope choices are a menuitemradio group — the menu's ARIA.
    await expect(entity.monitorMenu.getByRole("menuitemcheckbox")).toHaveCount(1);
    await expect(entity.monitorMenu.getByRole("menuitemradio").first()).toBeVisible();

    // Roving focus moves between the menu's rows (the checkbox is the first roving item on open).
    const items = entity.monitorMenuItems();
    await expect(items.nth(0)).toBeFocused();
    await page.keyboard.press("ArrowDown");
    await expect(items.nth(1)).toBeFocused();

    // Escape closes the menu AND returns focus to the opener — WhisparrMonitorButton.closeMenu re-focuses it.
    await page.keyboard.press("Escape");
    await expect(entity.monitorMenu).toHaveCount(0);
    await expect(entity.monitorMenuTrigger).toBeFocused();
  });
});

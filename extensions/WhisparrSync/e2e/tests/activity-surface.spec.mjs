// Drives the nested "Wanted, queue & history" activity sub-page (src/wanted/) through the running host,
// proving the read-only surface contract deterministically: the a11y tab bar (role=tablist, aria-selected,
// roving focus), the live count badges on Wanted + Queue, the virtualized rows per section with a queue
// progress bar, and the load-bearing DISTINCT states — a Whisparr outage renders the WHISPARR_UNAVAILABLE
// banner, NEVER a section's empty copy — plus the drop-on-import derivation (a wanted
// row disappears and the count decrements once it imports). The hermetic harness stands up no Whisparr, so
// this spec route-intercepts the three /activity/* projections with SYNTHETIC paged bodies (numeric
// "Scene NNNN" titles, no real metadata, no images): the same uniform camelCase shape a real v3 or v2 read
// returns, so the "Scenes" presentation this proves is version-agnostic. The real v3/v2 populated drive
// against the seeded clean containers and the real download→import loop are a live human check; this tier
// proves the rendering + state behavior deterministically.
// No real scene metadata ever enters this fixture.
import { test, expect } from "../lib/whisparrsync-fixtures.mjs";

// The parent WhisparrSync settings tab (deep-linkable); the activity sub-page is a nested child reached from
// its settings-nav entry (a contributed sub-page is not independently deep-linkable until its parent loads).
const PARENT = "/settings/whisparr-sync";

/** A content-safe synthetic scene row: numeric title only, optional display facts. */
function scene(n, extra = {}) {
  return {
    sceneTitle: `Scene ${String(n).padStart(4, "0")}`,
    studio: null,
    quality: null,
    date: null,
    ...extra,
  };
}

function page1(records, total) {
  return { page: 1, pageSize: 50, totalRecords: total, records };
}

test("activity sub-page renders three tabs with virtualized rows, count badges, queue progress, and distinct empty/error states (+ drop-on-import)", async ({
  page,
  baseUrl,
}, testInfo) => {
  // The mutable projection state the interceptors reflect back — mutating it then Refreshing exercises the
  // real load path (the store drops its in-flight dedupe and re-reads), exactly as a live derivation would.
  const state = {
    wanted: {
      outage: false,
      total: 3,
      rows: [
        scene(1, { studio: "Synthetic Studio", date: "2020-01-01T00:00:00Z" }),
        scene(2, { studio: "Synthetic Studio", date: "2020-02-01T00:00:00Z" }),
        scene(3, { studio: "Synthetic Studio", date: "2020-03-01T00:00:00Z" }),
      ].map((s) => ({ scene: s, addedDate: s.date })),
    },
    queue: {
      outage: false,
      total: 2,
      rows: [
        {
          scene: scene(10, { studio: "Synthetic Studio", quality: "1080p" }),
          state: "downloading",
          progressPercent: 42,
          eta: "00:12:00",
        },
        {
          scene: scene(11, { studio: "Synthetic Studio" }),
          state: "queued",
          progressPercent: null,
          eta: null,
        },
      ],
    },
    history: { outage: false, total: 0, rows: [] },
  };

  const intercept = (glob, section, toBody) =>
    page.route(glob, async (route) => {
      const s = state[section];
      if (s.outage) {
        await route.fulfill({
          status: 502,
          contentType: "application/json",
          body: JSON.stringify({ error: "unreachable" }),
        });
        return;
      }
      await route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(toBody(s)),
      });
    });

  await intercept("**/activity/wanted**", "wanted", (s) =>
    page1(s.rows, s.total),
  );
  await intercept("**/activity/queue**", "queue", (s) =>
    page1(s.rows, s.total),
  );
  await intercept("**/activity/history**", "history", (s) =>
    page1(s.rows, s.total),
  );

  await page.goto(`${baseUrl}${PARENT}`);
  // The parent tab loads the extension manifest, which reveals the nested sub-page's settings-nav entry; click
  // it to open the activity surface (a contributed sub-page resolves only once its parent has loaded).
  const subpageNav = page.getByRole("button", {
    name: /Wanted, queue & history/,
  });
  await subpageNav.click({ timeout: 20_000 });

  // ---- Tab bar a11y: a tablist of three tabs, Wanted active by default (hash default) ----
  const tablist = page.getByRole("tablist", { name: "Activity sections" });
  await expect(tablist).toBeVisible({ timeout: 20_000 });
  const wantedTab = page.getByRole("tab", { name: /Wanted/ });
  const queueTab = page.getByRole("tab", { name: /Queue/ });
  const historyTab = page.getByRole("tab", { name: /History/ });
  await expect(wantedTab).toHaveAttribute("aria-selected", "true");

  // ---- Wanted: 3 virtualized rows + a live count badge reading 3 ----
  const listitems = page.locator('[role="listitem"]');
  await expect.poll(() => listitems.count()).toBe(3);
  await expect(wantedTab).toContainText("3");
  await expect(page.getByText("Showing 3 of 3")).toBeVisible();

  // ---- Roving focus: ArrowRight from Wanted activates + focuses Queue ----
  await wantedTab.focus();
  await page.keyboard.press("ArrowRight");
  await expect(queueTab).toHaveAttribute("aria-selected", "true");
  await expect(queueTab).toBeFocused();

  // ---- Queue: 2 rows, a live count badge reading 2, and a progress bar (determinate + indeterminate) ----
  await expect.poll(() => listitems.count()).toBe(2);
  await expect(queueTab).toContainText("2");
  const bars = page.locator('[role="progressbar"]');
  await expect.poll(() => bars.count()).toBe(2);
  // The downloading row is determinate (aria-valuenow on the bar itself); the queued row is indeterminate
  // (aria-busy, no valuenow) — the two are distinct rendered states, not one collapsed bar.
  await expect(page.locator('[role="progressbar"][aria-valuenow]')).toHaveCount(
    1,
  );
  await expect(
    page.locator('[role="progressbar"][aria-busy="true"]'),
  ).toHaveCount(1);
  await page.screenshot({ path: testInfo.outputPath("queue-populated.png") });

  // ---- History: the populated-empty state renders its OWN copy (distinct from an outage) ----
  await historyTab.click();
  await expect(historyTab).toHaveAttribute("aria-selected", "true");
  await expect(page.getByText("No history yet")).toBeVisible();
  await expect(listitems).toHaveCount(0);

  // ---- error != empty: a Wanted outage renders the unavailable banner, NEVER "Nothing wanted" ----
  state.wanted.outage = true;
  await wantedTab.click();
  await page.getByRole("button", { name: "Refresh" }).first().click();
  await expect(page.getByText(/Whisparr isn.t reachable/)).toBeVisible();
  await expect(page.getByText("Nothing wanted")).toHaveCount(0);
  await page.screenshot({ path: testInfo.outputPath("wanted-outage.png") });

  // ---- Hermetic derivation: a wanted scene that imports drops out + the badge decrements ----
  state.wanted.outage = false;
  state.wanted.rows = state.wanted.rows.slice(1); // Scene 0001 "imported" → gained a file → no longer wanted
  state.wanted.total = 2;
  await page.getByRole("button", { name: "Refresh" }).first().click();
  await expect.poll(() => listitems.count()).toBe(2);
  await expect(wantedTab).toContainText("2");
  await expect(page.getByText("Scene 0001")).toHaveCount(0);
});

// Criterion for the removed fourth grouping: it is gone from the shipped bundle, not merely unrouted. The tab
// bar is DERIVED from the descriptor table, so its length is the honest count of what ships — and a console
// error is what a half-removed grouping would produce (a store slot or row arm keyed on a tab that no longer
// exists renders nothing and throws instead).
test("the tab bar carries exactly the three surviving groupings, and the page logs no console error", async ({
  page,
  baseUrl,
}) => {
  const consoleErrors = [];
  page.on("console", (msg) => {
    if (msg.type() === "error") consoleErrors.push(msg.text());
  });
  page.on("pageerror", (err) => consoleErrors.push(err.message));

  for (const section of ["wanted", "queue", "history"]) {
    await page.route(`**/activity/${section}**`, (route) =>
      route.fulfill({
        status: 200,
        contentType: "application/json",
        body: JSON.stringify(page1([], 0)),
      }),
    );
  }

  await page.goto(`${baseUrl}${PARENT}`);
  await page
    .getByRole("button", { name: /Wanted, queue & history/ })
    .click({ timeout: 20_000 });

  // Scoped to the activity tablist: an unscoped tab locator would count the HOST's own settings tabs too.
  const tablist = page.getByRole("tablist").first();
  await expect(tablist).toBeVisible({ timeout: 20_000 });
  const tabs = tablist.getByRole("tab");
  await expect.poll(() => tabs.count()).toBe(3);
  // Prefix-matched: Wanted and Queue carry a count badge inside the tab, so their accessible text is the
  // label followed by a number.
  await expect(tabs).toHaveText([/^Wanted/, /^Queue/, /^History$/]);

  // Every tab is reachable, so a removal that left a dead store slot surfaces here rather than on one tab only.
  for (const name of ["Wanted", "Queue", "History"]) {
    await tablist.getByRole("tab", { name }).click();
    await expect(tablist.getByRole("tab", { name })).toHaveAttribute("aria-selected", "true");
  }

  expect(consoleErrors, consoleErrors.join("\n")).toEqual([]);
});

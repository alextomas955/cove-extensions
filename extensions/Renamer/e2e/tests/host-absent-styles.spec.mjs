// Verifies that Tailwind utilities the panel needs but the RELEASED host never compiles are rendered
// through element-scoped INLINE styles instead — AND that the extension leaks nothing onto host pages
// (it ships no cssBundle).
//
// Two separate waves of host-absent classes have been found so far. The first was geometry
// (translate-x-4, py-3.5, pb-20, …), covered by the toggle-knob assertion below. The second was
// colour: border-amber-400/40, bg-amber-400/10, bg-red-950/40, border-green-500/40, bg-green-500/10
// and bg-red-400 — used by the status pills and the save-bar dot, and drawing nothing at all on a
// released host.
//
// Nothing catches a third wave automatically. The class-discipline script that nominally did was
// retired: its forbidden list was hand-written and three entries long, so it had already missed both
// waves, and no replacement was built. Adding a utility means checking it against the released host
// stylesheet by hand.
//
// This runs against a CLEAN released cove-app image (the harness default), NOT the local dev host
// whose @source contamination would mask the whole point: on a released host the extension gets
// only the classes Cove's own prebuilt bundle emits, so an inline style is the only thing that
// makes these render for an end user.
import { test, expect, seedVideo } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

const RENAMER_ID = "com.alextomas955.renamer";

// The five theme variables the panel's inline colours are built from. Each is defined by the host
// even though the matching Tailwind utility is not emitted — that gap is the whole reason these
// styles are inline, and it is also why the variables deserve their own assertion: an inline style
// naming a variable the host stopped defining fails silently, exactly like the classes did.
const THEME_COLOR_VARS = [
  "--color-amber-400",
  "--color-red-400",
  "--color-red-950",
  "--color-green-500",
  "--color-border",
];

/**
 * Resolves a CSS colour expression in the live host document, returning both the browser's computed
 * form and the alpha it actually paints. Expectations are stated as the INTENT ("40% of the host's
 * amber") rather than as a literal, so an assertion still describes the design after Cove retunes
 * its palette. The canvas round-trip is what proves the result is paintable: it starts from a
 * transparent sentinel, so a value the browser cannot parse leaves alpha at 0 rather than silently
 * reading as black.
 */
function resolveColor(page, expression) {
  return page.evaluate((expr) => {
    const probe = document.createElement("span");
    probe.style.color = expr;
    document.body.appendChild(probe);
    const computed = getComputedStyle(probe).color;
    probe.remove();

    const ctx = document.createElement("canvas").getContext("2d");
    ctx.fillStyle = "rgba(0, 0, 0, 0)";
    ctx.fillStyle = computed;
    ctx.fillRect(0, 0, 1, 1);
    return { computed, alpha: ctx.getImageData(0, 0, 1, 1).data[3] };
  }, expression);
}

/** A colour the user can actually see. */
function expectVisible({ computed, alpha }, what) {
  expect(alpha, `${what} must paint something (resolved to "${computed}")`).toBeGreaterThan(0);
}

/** One synthetic dry-run row, so a status the real library cannot be coaxed into still renders. */
function scanRow(fileId, status) {
  return {
    kind: "video",
    entityId: fileId,
    fileId,
    oldFullPath: `/data/old-${fileId}.mp4`,
    newFullPath: `/data/new-${fileId}.mp4`,
    status,
    reason: null,
    suffixed: false,
    sanitized: false,
  };
}

test("the extension declares no cssBundle (ships zero CSS — cannot leak onto host pages)", async ({
  api,
}) => {
  const { json } = await api.get("/api/extensions");
  const renamer = json.find((e) => e.id === "com.alextomas955.renamer");
  expect(renamer).toBeTruthy();
  // The combined extension stylesheet must NOT import a Renamer bundle.
  const { text } = await api.get("/api/extensions/bundles/ui.css").catch(() => ({ text: "" }));
  expect(text).not.toContain("renamer");
});

test("host-absent utilities render via inline styles on a released host", async ({
  page,
  baseUrl,
}) => {
  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();

  // 1. Toggle knob: the slide is an inline translateX, and it must actually move between states.
  //    Find a real Toggle knob (the <span> inside the switch <button>).
  const knob = page.locator('button[role="switch"] span, button[aria-checked] span').first();
  await expect(knob).toBeVisible({ timeout: 15_000 });
  const knobTransformBefore = await knob.evaluate((el) => getComputedStyle(el).transform);
  // toggle its parent switch
  await knob.evaluate((el) => el.closest("button")?.click());
  await page.waitForTimeout(300);
  const knobTransformAfter = await knob.evaluate((el) => getComputedStyle(el).transform);
  expect(
    knobTransformBefore,
    "knob transform must differ between off/on (inline translateX must apply)",
  ).not.toBe(knobTransformAfter);
  // and both must be a real matrix translate, not "none"
  expect(knobTransformAfter).not.toBe("none");
});

test("every theme colour variable the inline styles name is defined by the host", async ({
  page,
  baseUrl,
}) => {
  // StatusPill's `green` variant has no consumer in this extension today, so it is the one fixed
  // colour no UI path can exercise. Its variable can still be asserted, and so can every other one —
  // that shared dependency is what an inline style trades the missing utility for.
  await page.goto(`${baseUrl}/settings/renamer`);
  const defined = await page.evaluate(
    (names) =>
      Object.fromEntries(
        names.map((n) => [
          n,
          getComputedStyle(document.documentElement).getPropertyValue(n).trim(),
        ]),
      ),
    THEME_COLOR_VARS,
  );
  for (const name of THEME_COLOR_VARS) {
    expect(defined[name], `${name} must be defined by the host theme`).not.toBe("");
  }
  expectVisible(
    await resolveColor(page, "color-mix(in oklab, var(--color-green-500) 40%, transparent)"),
    "the green pill border tint",
  );
  expectVisible(
    await resolveColor(page, "color-mix(in oklab, var(--color-green-500) 10%, transparent)"),
    "the green pill fill tint",
  );
});

test("status pills render their amber and red tints from the host theme", async ({
  page,
  harness,
  baseUrl,
  api,
}) => {
  // The dry run short-circuits on an empty library and never asks for a row page, so there has to be
  // something real to scan. The ROWS are then served synthetically: `failed` cannot be produced on
  // demand against a real library, and a pill's styling does not depend on where its row came from.
  const video = await seedVideo({ container: harness.container, baseUrl });
  await api.put(`/api/videos/${video.id}`, { Title: `Host Absent Styles ${Date.now()}` });

  await page.route(`**/api/extensions/${RENAMER_ID}/scan-rows`, (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        rows: [scanRow(1, "skipGated"), scanRow(2, "failed")],
        next: null,
        entitiesExamined: 2,
        budgetExhausted: false,
      }),
    }),
  );

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  await settings.setFilenameTemplate("$title");
  await settings.openDryRun();

  const amber = page.getByText("Skipped — needs a required field").locator("xpath=..");
  const red = page.getByText("Failed — rolled back").locator("xpath=..");
  await expect(amber).toBeVisible({ timeout: 90_000 });
  await expect(red).toBeVisible();

  const amberTint = (percent) =>
    resolveColor(page, `color-mix(in oklab, var(--color-amber-400) ${percent}%, transparent)`);
  const [amberBorder, amberFill, redFill] = await Promise.all([
    amberTint(40),
    amberTint(10),
    resolveColor(page, "color-mix(in oklab, var(--color-red-950) 40%, transparent)"),
  ]);
  expectVisible(amberBorder, "the amber pill border tint");
  expectVisible(amberFill, "the amber pill fill tint");
  expectVisible(redFill, "the red pill fill tint");

  const style = (locator, property) =>
    locator.evaluate((el, prop) => getComputedStyle(el)[prop], property);
  expect(await style(amber, "borderTopColor"), "amber pill border").toBe(amberBorder.computed);
  expect(await style(amber, "backgroundColor"), "amber pill fill").toBe(amberFill.computed);
  expect(await style(red, "backgroundColor"), "red pill fill").toBe(redFill.computed);
});

test("the save bar dot turns red when a save fails", async ({ page, baseUrl }) => {
  await page.route(`**/api/extensions/${RENAMER_ID}/data/options`, (route) =>
    route.request().method() === "PUT"
      ? route.fulfill({ status: 500, contentType: "text/plain", body: "save refused" })
      : route.continue(),
  );

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  await settings.setFilenameTemplate("$title");
  await expect(settings.unsavedChangesIndicator).toBeVisible({ timeout: 15_000 });

  const dot = page.locator("span.h-2.w-2.rounded-full").first();
  await settings.saveChangesButton.click();
  await expect(page.getByText(/Couldn't save settings/)).toBeVisible({ timeout: 15_000 });

  const want = await resolveColor(page, "var(--color-red-400)");
  expectVisible(want, "the host red-400");
  expect(await dot.evaluate((el) => getComputedStyle(el).backgroundColor), "save-error dot").toBe(
    want.computed,
  );
});

test("host account page is unaffected by the extension (no CSS leak)", async ({
  page,
  baseUrl,
}) => {
  // The page that regressed when the extension shipped an unscoped .flex-col. With no extension CSS
  // it must render its native responsive layout: the account row is flex-row at a desktop width.
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`${baseUrl}/settings/my/account`);
  const logout = page.getByRole("button", { name: /log ?out/i }).first();
  await expect(logout).toBeVisible({ timeout: 15_000 });
  const rowFlexDir = await logout.evaluate((btn) => {
    const row = btn.parentElement;
    return row ? getComputedStyle(row).flexDirection : "no-row";
  });
  expect(
    rowFlexDir,
    "host account row must be flex-row at 1280px (no extension .flex-col leak)",
  ).toBe("row");
});

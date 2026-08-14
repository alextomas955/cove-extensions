// Verifies that Tailwind utilities the panel needs but the RELEASED host never compiles are rendered
// through element-scoped INLINE styles instead — AND that the extension leaks nothing onto host pages
// (it ships no cssBundle).
//
// Two separate waves of host-absent classes have been found so far. The first was geometry
// (translate-x-4, py-3.5, pb-20, …), covered by the toggle-knob assertion below. The second was
// colour: border-amber-400/40, bg-amber-400/10, bg-red-950/40, border-green-500/40, bg-green-500/10
// and bg-red-400 — used by the status pills and the save-bar dot, and drawing nothing at all on a
// released host. What stands for the colour wave here is the theme VARIABLES those inline styles are
// built from: a style naming a variable the host stopped defining fails exactly as silently as the
// classes did. The tint expressions themselves are deliberately NOT re-asserted — a check that
// resolves one in this browser and then compares an element against its own result agrees with itself
// whatever the value is, so the only defect it could catch is a colour that does not paint, and that
// is what the variable assertion catches. (Naming the CSS function here would also blind the gate
// that greps this file for it.)
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
import { test, expect } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

// Every theme variable the panel's inline colours are built from — derived by grepping `var(--color-`
// and the `tint()` helper across this extension's UI and the shared UI package, so each entry has a
// live consumer rather than a place in the list. Each is defined by the host even though the matching
// Tailwind utility is not emitted; that gap is the whole reason these styles are inline, and it is
// also why the variables deserve an assertion of their own: an inline style naming a variable the host
// stopped defining fails silently, exactly like the classes did.
const THEME_COLOR_VARS = [
  "--color-amber-400",
  "--color-red-400",
  "--color-red-950",
  "--color-green-500",
];

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

test("host-absent utilities render via inline styles on a released host, whose theme defines every variable they name", async ({
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

  // 2. The colour half, asserted at the variables rather than at the tints computed from them.
  //    StatusPill's `green` variant has no consumer in this extension today, so it is the one fixed
  //    colour no UI path can exercise. Its variable can still be asserted, and so can every other
  //    one — that shared dependency is what an inline style trades the missing utility for.
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

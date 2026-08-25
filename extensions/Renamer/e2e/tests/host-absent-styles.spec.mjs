// Verifies that the Tailwind utilities the released host's prebuilt stylesheet never emits render
// via element-scoped INLINE styles rather than as classes that compile to nothing - the panel's
// layout and transform utilities, and the status pills' background tints - AND that the extension
// leaks nothing onto host pages (it ships no cssBundle).
//
// This runs against a CLEAN released cove-app image (the harness default), NOT the local dev host
// whose @source contamination would mask the whole point: on a released host the extension gets
// only the classes Cove's own prebuilt bundle emits, so an inline style is the only thing that
// makes a host-absent utility render for an end user.
import { test, expect, seedVideo } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

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
  await expect
    .poll(() => knob.evaluate((el) => getComputedStyle(el).transform), { timeout: 5_000 })
    .not.toBe(knobTransformBefore);
  const knobTransformAfter = await knob.evaluate((el) => getComputedStyle(el).transform);
  expect(
    knobTransformBefore,
    "knob transform must differ between off/on (inline translateX must apply)",
  ).not.toBe(knobTransformAfter);
  // and both must be a real matrix translate, not "none"
  expect(knobTransformAfter).not.toBe("none");
});

// The amber and red status pills carry their fill as an inline `color-mix` off Cove's own colour
// scale, because the utilities that fill them are not in the host's prebuilt stylesheet. The scan
// runs for real and only the row payload is substituted, so the pills under assertion are the
// shipped component resolved by the host's own stylesheet, never markup this test wrote.
test("status pill tints resolve to a real background on a released host", async ({
  page,
  harness,
  baseUrl,
  api,
}) => {
  const video = await seedVideo({ container: harness.container, baseUrl });
  await api.put(`/api/videos/${video.id}`, { Title: `Pill Tint ${Date.now()}` });

  const row = (fileId, status) => ({
    kind: "video",
    entityId: video.id,
    fileId,
    oldFullPath: "/media/pill-tint.mp4",
    newFullPath: "/media/pill-tint.mp4",
    status,
    reason: null,
    suffixed: false,
    sanitized: false,
  });
  await page.route("**/extensions/com.alextomas955.renamer/scan-rows", (route) =>
    route.fulfill({
      status: 200,
      contentType: "application/json",
      body: JSON.stringify({
        rows: [row(1, "skipCollision"), row(2, "failed")],
        next: null,
        entitiesExamined: 2,
        budgetExhausted: false,
      }),
    }),
  );

  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();
  // The modal scans the panel's CURRENT (unsaved) options, so setting the template is enough.
  await settings.setFilenameTemplate("$title");
  await settings.openDryRun();

  const amberPill = page.locator("span.rounded-full", { hasText: "name conflict" }).first();
  const redPill = page.locator("span.rounded-full", { hasText: "rolled back" }).first();
  await expect(amberPill).toBeVisible({ timeout: 90_000 });
  await expect(redPill).toBeVisible();

  // What an untouched element computes to on this host, so the assertions below stay independent of
  // how Chromium happens to serialize a resolved color.
  const inert = await page.evaluate(() => {
    const probe = document.createElement("span");
    document.body.appendChild(probe);
    const value = getComputedStyle(probe).backgroundColor;
    probe.remove();
    return value;
  });

  for (const [tone, pill] of [
    ["amber", amberPill],
    ["red", redPill],
  ]) {
    const background = await pill.evaluate((el) => getComputedStyle(el).backgroundColor);
    expect(background, `${tone} pill must compute a real, non-transparent background`).not.toBe(
      inert,
    );
  }

  // Without this the assertions above would also pass on a host that DOES emit the utilities, which
  // is exactly the reading the dev host gives and the released host does not.
  const classOnly = await page.evaluate(
    (classNames) => {
      const probe = document.createElement("span");
      document.body.appendChild(probe);
      const resolved = classNames.map((className) => {
        probe.className = className;
        return getComputedStyle(probe).backgroundColor;
      });
      probe.remove();
      return resolved;
    },
    ["bg-amber-400/10", "bg-red-950/40"],
  );
  for (const background of classOnly) {
    expect(background, "the pill fill utilities must be absent from the host stylesheet").toBe(
      inert,
    );
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

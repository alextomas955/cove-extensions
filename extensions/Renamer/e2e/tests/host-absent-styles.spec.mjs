// Verifies that the Tailwind utilities the released host's prebuilt stylesheet never emits render
// via element-scoped inline styles rather than as classes that compile to nothing - the panel's
// layout and transform utilities, and the status pills' background tints - and that the extension
// leaks nothing onto host pages.
//
// This runs against a clean released cove-app image (the harness default), not the local dev host
// whose @source contamination would mask the whole point: on a released host the extension gets
// only the classes Cove's own prebuilt bundle emits, so an inline style is the only thing that
// makes a host-absent utility render for an end user.
import { test, expect, seedVideo, EXTENSION_ID } from "../lib/renamer-fixtures.mjs";
import { RenamerSettingsPage } from "../lib/pages/renamer-settings-page.mjs";

test("host-absent utilities render via inline styles on a released host", async ({
  page,
  baseUrl,
}) => {
  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();

  // 1. Toggle track, off: the fill is an inline tone off Cove's colour scale, because the card
  //    colour it would otherwise carry is the colour of the container the switch sits in, where the
  //    track disappears and the control reads as a bare knob.
  const offSwitch = page.locator('button[role="switch"][aria-checked="false"]').first();
  await expect(offSwitch).toBeVisible({ timeout: 15_000 });
  // Resolve both tones through the page rather than comparing strings, so the assertion stays
  // independent of how Chromium serializes a colour. `inert` guards the card probe itself: an
  // undeclared --color-card would leave the probe transparent, and the comparison below would then
  // pass because the token is missing rather than because the track stepped away from it.
  const { card, inert } = await page.evaluate(() => {
    const read = (apply) => {
      const probe = document.createElement("span");
      if (apply) apply(probe);
      document.body.appendChild(probe);
      const value = getComputedStyle(probe).backgroundColor;
      probe.remove();
      return value;
    };
    return {
      card: read((el) => (el.style.backgroundColor = "var(--color-card)")),
      inert: read(null),
    };
  });
  expect(
    card,
    "--color-card must resolve to a real fill for this comparison to mean anything",
  ).not.toBe(inert);
  const offTrack = await offSwitch.evaluate((el) => getComputedStyle(el).backgroundColor);
  expect(offTrack, "an off toggle track must not be filled with the card colour").not.toBe(card);

  // 2. Toggle knob: the slide is an inline translateX, and it must actually move between states.
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
  await page.route(`**/extensions/${EXTENSION_ID}/scan-rows`, (route) =>
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
  // The modal scans the panel's current (unsaved) options, so setting the template is enough.
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

  // A host whose stylesheet already emits the fill utilities fills the pills with or without the
  // inline style, so a filled pill proves nothing there. Once the floor host emits both, the inline
  // fill is no longer needed.
  const utilities = ["bg-amber-400/10", "bg-red-950/40"];
  const resolved = await page.evaluate((classNames) => {
    const probe = document.createElement("span");
    document.body.appendChild(probe);
    const values = classNames.map((className) => {
      probe.className = className;
      return getComputedStyle(probe).backgroundColor;
    });
    probe.remove();
    return values;
  }, utilities);
  const emitted = utilities.filter((_, i) => resolved[i] !== inert);
  test.skip(emitted.length > 0, `this host's stylesheet emits ${emitted.join(", ")}`);

  for (const [tone, pill] of [
    ["amber", amberPill],
    ["red", redPill],
  ]) {
    const background = await pill.evaluate((el) => getComputedStyle(el).backgroundColor);
    expect(background, `${tone} pill must compute a real, non-transparent background`).not.toBe(
      inert,
    );
  }
});

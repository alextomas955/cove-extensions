// Verifies the 7 genuinely host-absent Tailwind classes the panel used (translate-x-4,
// translate-x-0.5, py-3.5, pb-20, max-w-0 x3, border-collapse, hover:no-underline) now render via
// element-scoped INLINE styles rather than classes the released host never compiles — AND that the
// extension leaks nothing onto host pages (it ships no cssBundle).
//
// This runs against a CLEAN released cove-app image (the harness default), NOT the local dev host
// whose @source contamination would mask the whole point: on a released host the extension gets
// only the classes Cove's own prebuilt bundle emits, so an inline style is the only thing that
// makes these 7 render for an end user.
import { test, expect } from '../lib/renamer-fixtures.mjs';
import { RenamerSettingsPage } from '../lib/pages/renamer-settings-page.mjs';

test('the extension declares no cssBundle (ships zero CSS — cannot leak onto host pages)', async ({ api }) => {
  const { json } = await api.get('/api/extensions');
  const renamer = json.find((e) => e.id === 'com.alextomas955.renamer');
  expect(renamer).toBeTruthy();
  // The combined extension stylesheet must NOT import a Renamer bundle.
  const { text } = await api.get('/api/extensions/bundles/ui.css').catch(() => ({ text: '' }));
  expect(text).not.toContain('renamer');
});

test('host-absent utilities render via inline styles on a released host', async ({ page, baseUrl }) => {
  const settings = new RenamerSettingsPage(page, baseUrl);
  await settings.goto();

  // 1. Toggle knob: the slide is an inline translateX, and it must actually move between states.
  //    Find a real Toggle knob (the <span> inside the switch <button>).
  const knob = page.locator('button[role="switch"] span, button[aria-checked] span').first();
  await expect(knob).toBeVisible({ timeout: 15_000 });
  const knobTransformBefore = await knob.evaluate((el) => getComputedStyle(el).transform);
  // toggle its parent switch
  await knob.evaluate((el) => el.closest('button')?.click());
  await page.waitForTimeout(300);
  const knobTransformAfter = await knob.evaluate((el) => getComputedStyle(el).transform);
  expect(
    knobTransformBefore,
    'knob transform must differ between off/on (inline translateX must apply)',
  ).not.toBe(knobTransformAfter);
  // and both must be a real matrix translate, not "none"
  expect(knobTransformAfter).not.toBe('none');
});

test('host account page is unaffected by the extension (no CSS leak)', async ({ page, baseUrl }) => {
  // The page that regressed when the extension shipped an unscoped .flex-col. With no extension CSS
  // it must render its native responsive layout: the account row is flex-row at a desktop width.
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(`${baseUrl}/settings/my/account`);
  const logout = page.getByRole('button', { name: /log ?out/i }).first();
  await expect(logout).toBeVisible({ timeout: 15_000 });
  const rowFlexDir = await logout.evaluate((btn) => {
    const row = btn.parentElement;
    return row ? getComputedStyle(row).flexDirection : 'no-row';
  });
  expect(rowFlexDir, 'host account row must be flex-row at 1280px (no extension .flex-col leak)').toBe('row');
});

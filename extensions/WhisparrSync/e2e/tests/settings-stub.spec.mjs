// The settings tab this extension contributes, asserted against a live Cove.
//
// The subject is the stub's SENTENCE, never the extension's name. The host draws the tab header from
// the manifest, so a name assertion passes just as happily against a bundle that never loaded; the
// sentence exists only inside the component this extension ships, and reaching it means the host
// resolved the manifest's componentName to the bundle's component-map key.
import { test, expect } from "../lib/whisparr-sync-fixtures.mjs";

const SETTINGS_PATH = "/settings/whisparr-sync";
const STUB_SENTENCE = "Connection setup for Whisparr Sync arrives in a later release.";

// Each navigation's own wait. It has to cover a cold container serving the extension bundle for the
// first time under a loaded runner, which is slow rather than broken and raises no signal to wait on.
const ATTEMPT_BUDGET_MS = 60_000;

// How many navigations the visit is allowed. More time does not recover the host behaviour below;
// only a fresh navigation does, and an unbounded retry turns a permanent failure into a hung test.
const ATTEMPTS = 3;

test("the settings tab renders this extension's own stub", async ({ page, baseUrl }) => {
  const panelUrl = `${baseUrl}${SETTINGS_PATH}`;
  const stub = page.getByText(STUB_SENTENCE, { exact: true });

  // The path is not one of the host's own routes. The host carries the unknown key only until it
  // finishes loading extensions, then resolves it against the settings tabs that load produced - and
  // answers a load that produced none by switching to its first built-in tab and rewriting the
  // address to match. Nothing after that rewrite can reach this panel, because the address no longer
  // names the extension.
  for (let attempt = 1; attempt <= ATTEMPTS; attempt++) {
    await page.goto(panelUrl);
    const rendered = await stub
      .waitFor({ state: "visible", timeout: ATTEMPT_BUDGET_MS })
      .then(() => true)
      .catch(() => false);
    if (rendered) break;
  }

  await expect(
    stub,
    `the stub sentence never rendered at ${panelUrl} across ${ATTEMPTS} navigation(s); the page is now at ${page.url()}`,
  ).toBeVisible();
});

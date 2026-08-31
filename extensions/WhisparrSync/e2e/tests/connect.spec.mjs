// The connect path, end to end, on BOTH generations: a user types an address and a key into the
// settings tab, presses Test connection, and the page reports what answered.
//
// The Whisparr instance is a real container on Cove's own network, so the request under test leaves
// Cove's process, crosses the network and is answered by the application itself. The address typed is
// the instance's IN-NETWORK one: Cove has no route to the host-published port a test process uses.
//
// The success assertion is the instance's OWN version string, taken from the running container rather
// than written here, and then also asserted to be the literal this suite was pinned against. Either
// alone would be weaker: a value read back from the instance agrees with whatever it says, and a bare
// literal stops describing the instance the day the image moves.
//
// Both generations run the same three legs against their own instance. Each generation's block starts
// only its own container, so neither pays for the other's boot.
import { test, expect } from "../lib/whisparr-sync-fixtures.mjs";

const SETTINGS_PATH = "/settings/whisparr-sync";
const STATUS_PATH = "/api/v3/system/status";

// The builds this extension's classifier pins were transcribed from. Hand-written here, so a moved
// image fails loudly instead of the suite quietly agreeing with whatever answered.
const PINNED_VERSIONS = { v3: "3.3.8.1097", v2: "2.2.0.231" };

// A key of the right shape that no instance was seeded with.
const WRONG_KEY = "0000000000000000000000000000dead";

// A port nothing inside the Cove container listens on. Well formed, so the address check passes and a
// request really is attempted, and refused at once rather than left to time out.
const DEAD_ADDRESS = "http://127.0.0.1:1";

// A cold container serving the extension bundle for the first time is slow rather than broken and
// raises no signal to wait on.
const ATTEMPT_BUDGET_MS = 60_000;
const ATTEMPTS = 3;

/**
 * Opens the panel on `generation`'s card and returns its controls.
 *
 * The path is not one of the host's own routes. The host carries the unknown key only until it
 * finishes loading extensions, then answers a load that produced no matching tab by switching to its
 * first built-in tab and rewriting the address. Nothing after that rewrite can reach this panel, and
 * only a fresh navigation recovers it.
 *
 * The page opens on the generation Cove is set to use, which is v3 until a save moves it. Reaching
 * the other card is the page's own Switch affordance, so a leg below runs against the card a user
 * would be looking at.
 */
async function openPanel(page, baseUrl, generation) {
  const panelUrl = `${baseUrl}${SETTINGS_PATH}`;
  const addressField = page.getByPlaceholder("http://whisparr:6969");
  for (let attempt = 1; attempt <= ATTEMPTS; attempt++) {
    await page.goto(panelUrl);
    const rendered = await addressField
      .waitFor({ state: "visible", timeout: ATTEMPT_BUDGET_MS })
      .then(() => true)
      .catch(() => false);
    if (rendered) break;
  }
  await expect(
    addressField,
    `the connection panel never rendered at ${panelUrl} across ${ATTEMPTS} navigation(s); the page is now at ${page.url()}`,
  ).toBeVisible();

  if (generation !== "v3") {
    const otherCard = page.getByRole("button", { name: "Switch" });
    await expect(
      otherCard,
      `the ${generation} card offered no Switch, so the form is still editing another generation`,
    ).toBeVisible();
    await otherCard.click();
    await expect(page.getByText("Editing", { exact: false })).toBeVisible();
  }

  return {
    addressField,
    keyField: page.locator('input[type="password"]'),
    testButton: page.getByRole("button", { name: "Test connection" }),
  };
}

for (const generation of ["v3", "v2"]) {
  test.describe(`Whisparr ${generation}`, () => {
    // Only this generation's container, so a block pays for one instance rather than two.
    test.use({ whisparrGenerations: [generation] });

    test(`the panel reports the ${generation} instance's own version string for a good address and key`, async ({
      page,
      baseUrl,
      whisparr,
    }) => {
      // What the instance says about itself, asked directly, so the assertion below is against the
      // running container rather than against a value this file invented.
      const status = await whisparr.apiFor(generation).get(STATUS_PATH);
      expect(status.status, `the fixture did not answer ${STATUS_PATH}: ${status.text}`).toBe(200);
      const reported = status.json.version;
      expect(
        reported,
        `the ${generation} fixture is not the build this extension's pins were transcribed from`,
      ).toBe(PINNED_VERSIONS[generation]);

      const instance = whisparr[generation];
      const { addressField, keyField, testButton } = await openPanel(page, baseUrl, generation);
      await addressField.fill(instance.internalBaseUrl);
      await keyField.fill(whisparr.apiKey);
      await testButton.click();

      await expect(
        page.getByText(`Connected to Whisparr ${reported}`, { exact: false }),
        `the panel never reported ${reported} for ${instance.internalBaseUrl}`,
      ).toBeVisible({ timeout: ATTEMPT_BUDGET_MS });

      // The generation, so a pass cannot mean the panel merely echoed a version it never classified,
      // and so that one generation's instance cannot pass as the other's.
      await expect(page.getByText(`(${generation})`, { exact: false })).toBeVisible();
    });

    test(`a wrong key on ${generation} reads as a turned-down key rather than an unreachable address`, async ({
      page,
      baseUrl,
      whisparr,
    }) => {
      const { addressField, keyField, testButton } = await openPanel(page, baseUrl, generation);
      await addressField.fill(whisparr[generation].internalBaseUrl);
      await keyField.fill(WRONG_KEY);
      await testButton.click();

      // The refusal kinds are never collapsed: this instance is reachable and answered, so anything
      // naming the address as unanswered would be the wrong one of the four.
      await expect(
        page.getByText("Whisparr turned that API key down.", { exact: false }),
        "the panel did not report the key as turned down",
      ).toBeVisible({ timeout: ATTEMPT_BUDGET_MS });
      await expect(page.getByText("Nothing answered at", { exact: false })).toHaveCount(0);
    });

    test(`an address nothing listens on, on the ${generation} card, reads as unreachable`, async ({
      page,
      baseUrl,
      whisparr,
    }) => {
      const { addressField, keyField, testButton } = await openPanel(page, baseUrl, generation);
      await addressField.fill(DEAD_ADDRESS);
      await keyField.fill(whisparr.apiKey);
      await testButton.click();

      // The other half of the pair above, and it has to read differently: a user sent to the key when
      // the address is wrong changes the one setting that was right.
      await expect(
        page.getByText(`Nothing answered at ${DEAD_ADDRESS}`, { exact: false }),
        "the panel did not report the address as unanswered",
      ).toBeVisible({ timeout: ATTEMPT_BUDGET_MS });
      await expect(page.getByText("Whisparr turned that API key down.")).toHaveCount(0);
    });
  });
}

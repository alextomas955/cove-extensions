// The whole settings page against a live Cove with BOTH Whisparr generations running: two cards,
// the switch, the callback edit, and the reload.
//
// One test rather than several. The settings this writes are instance-global, so splitting the
// sequence into separate tests would either share one Cove between them - making the order they run
// in part of what is asserted - or pay for a second container pair to say the same thing.
//
// Its own Cove, for the same reason. The worker-shared harness is read by sibling spec files, and a
// saved connection would leak into theirs.
//
// Every "nothing was written" claim is read back through the extension's own settings route rather
// than off the page, because the page is what is under test.
import {
  test as base,
  expect,
  createApiClient,
  isolatedHarnessFixture,
} from "@cove-extensions/e2e";
import { startWhisparr } from "@cove-extensions/e2e/whisparr";
import { WHISPARR_SYNC_EXTENSION } from "../lib/whisparr-sync-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const SETTINGS_PATH = `/api/extensions/${EXTENSION_ID}/settings`;
const PANEL_PATH = "/settings/whisparr-sync";
const STATUS_PATH = "/api/v3/system/status";

// A host that resolves on the shared network, so Whisparr's own save-time connection test of the
// registered address does not refuse it - a refusal would measure that rather than the edit.
const EDITED_CALLBACK_HOST = "http://cove:5073";

// A cold container serving the extension bundle for the first time is slow rather than broken and
// raises no signal to wait on.
const ATTEMPT_BUDGET_MS = 60_000;
const ATTEMPTS = 3;

const test = base.extend({
  isolatedHarness: isolatedHarnessFixture(WHISPARR_SYNC_EXTENSION),

  // Both generations, on the isolated Cove's own network. Started here rather than through the
  // shared `whisparr` fixture, which binds to the worker harness this spec deliberately does not use.
  whisparrPair: async ({ isolatedHarness }, use) => {
    const instances = await startWhisparr({
      network: isolatedHarness.container.getNetworkNames()[0],
      generations: ["v3", "v2"],
    });
    try {
      await use(instances);
    } finally {
      await instances.stop();
    }
  },

  baseUrl: async ({ isolatedHarness }, use) => {
    await use(isolatedHarness.baseUrl);
  },
});

// Twelve minutes: this starts a Cove, a Postgres and two Whisparr containers before it asserts
// anything, and each is a cold boot.
test.setTimeout(12 * 60_000);

/**
 * Opens the panel and returns its controls.
 *
 * The path is not one of the host's own routes. The host carries the unknown key only until it
 * finishes loading extensions, then answers a load that produced no matching tab by switching to its
 * first built-in tab and rewriting the address. Nothing after that rewrite can reach this panel, and
 * only a fresh navigation recovers it.
 */
async function openPanel(page, baseUrl) {
  const addressField = page.getByPlaceholder("http://whisparr:6969");
  for (let attempt = 1; attempt <= ATTEMPTS; attempt++) {
    await page.goto(`${baseUrl}${PANEL_PATH}`);
    const rendered = await addressField
      .waitFor({ state: "visible", timeout: ATTEMPT_BUDGET_MS })
      .then(() => true)
      .catch(() => false);
    if (rendered) break;
  }
  await expect(
    addressField,
    `the connection panel never rendered across ${ATTEMPTS} navigation(s); the page is now at ${page.url()}`,
  ).toBeVisible();

  return {
    addressField,
    keyField: page.locator('input[type="password"]'),
    callbackField: page.getByLabel("Callback address"),
    testButton: page.getByRole("button", { name: "Test connection" }),
    saveButton: page.getByRole("button", { name: "Save connection" }),
    switchButton: page.getByRole("button", { name: "Switch" }),
    registerButton: page.getByRole("button", { name: "Register in Whisparr" }),
  };
}

/** The version an instance reports about itself, asked directly rather than through the extension. */
async function reportedVersion(whisparr, generation) {
  const status = await whisparr.apiFor(generation).get(STATUS_PATH);
  expect(status.status, `the ${generation} fixture did not answer ${STATUS_PATH}`).toBe(200);
  return status.json.version;
}

/**
 * A value planted on the window that a page load destroys.
 *
 * Reloading is the assertion, and it is one nothing on the page reports. A marker that is gone
 * afterwards is the load having happened; a marker still there is it not having happened.
 */
const MARKER = "__coveSettingsSliceMarker";
const plantMarker = (page) => page.evaluate((name) => (window[name] = 1), MARKER);
const readMarker = (page) => page.evaluate((name) => window[name], MARKER);

test("both generations are configured independently, and only a generation change reloads", async ({
  page,
  baseUrl,
  isolatedHarness,
  whisparrPair,
}) => {
  const owner = createApiClient(
    () => baseUrl,
    () => isolatedHarness.token,
  );
  const storedSettings = async () => {
    const read = await owner.get(SETTINGS_PATH);
    expect(read.status, `GET ${SETTINGS_PATH} answered: ${read.text.slice(0, 300)}`).toBe(200);
    return read.json;
  };

  const v3Version = await reportedVersion(whisparrPair, "v3");
  const v2Version = await reportedVersion(whisparrPair, "v2");
  const v3Address = whisparrPair.v3.internalBaseUrl;
  const v2Address = whisparrPair.v2.internalBaseUrl;

  const panel = await openPanel(page, baseUrl);

  await test.step("connecting on one card leaves the other card's stored values untouched", async () => {
    await panel.addressField.fill(v3Address);
    await panel.keyField.fill(whisparrPair.apiKey);
    await panel.testButton.click();
    await expect(
      page.getByText(`Connected to Whisparr ${v3Version}`, { exact: false }),
      `the panel never reported ${v3Version} for ${v3Address}`,
    ).toBeVisible({ timeout: ATTEMPT_BUDGET_MS });

    await panel.saveButton.click();
    await expect(page.getByText("Connection saved.", { exact: true })).toBeVisible({
      timeout: ATTEMPT_BUDGET_MS,
    });

    const stored = await storedSettings();
    expect(stored.selectedGeneration).toBe("v3");
    expect(stored.v3.address).toBe(v3Address);
    expect(stored.v3.keyIsSet).toBe(true);
    // The discriminating half: the other generation was never named by that save, and a page that
    // wrote one form to both would show here and nowhere else.
    expect(stored.v2.address, "saving one card also wrote the other card's address").toBe("");
    expect(stored.v2.keyIsSet, "saving one card also wrote the other card's key").toBe(false);
  });

  await test.step("testing the connection as stored records the version the two lines report", async () => {
    // The key field is blank now, and correctly so: a saved key is never handed back to the browser.
    // Pressing Test in that state asks about the STORED connection, which is the only test whose
    // answer updates the recorded version.
    await expect(panel.keyField).toHaveValue("");
    await panel.testButton.click();

    await expect(
      page.getByText(`Whisparr reported ${v3Version} · verified`, { exact: false }),
      "a test against the stored address did not record the version the recorded line reports",
    ).toHaveCount(1, { timeout: ATTEMPT_BUDGET_MS });
    // The second line, which measures something else and is never merged into the first.
    await expect(page.getByText("Whisparr last reachable", { exact: false })).toBeVisible();
    // The other generation was not verified by that test, and its own card still says so rather than
    // borrowing the reading.
    await expect(
      page.getByText("Whisparr version not verified yet", { exact: true }),
      "verifying one generation reported the other as verified too",
    ).toBeVisible();
  });

  await test.step("a test that reaches the other generation names the version and writes nothing", async () => {
    await panel.addressField.fill(v2Address);
    // A key typed with it: an address the form has changed is tested as a pair, because the stored
    // key belongs to a different address and the browser has no copy of it to send anyway.
    await panel.keyField.fill(whisparrPair.apiKey);
    await panel.testButton.click();

    await expect(
      page.getByText(`answered as Whisparr v2 ${v2Version}`, { exact: false }),
      `the panel did not name the ${v2Version} instance the v3 card reached`,
    ).toBeVisible({ timeout: ATTEMPT_BUDGET_MS });
    await expect(page.getByText("Nothing was saved", { exact: false })).toBeVisible();

    const stored = await storedSettings();
    expect(stored.selectedGeneration, "a cross-generation detection changed the selection").toBe(
      "v3",
    );
    expect(stored.v2.address, "a cross-generation detection stored the other connection").toBe("");
    expect(stored.v3.address, "a cross-generation detection overwrote the tested card").toBe(
      v3Address,
    );
  });

  await test.step("switching cards discards the unsaved edit with no dialog", async () => {
    // The v2 address is still in the v3 card's field from the step above, and was never saved.
    await expect(panel.addressField).toHaveValue(v2Address);
    await panel.switchButton.click();

    await expect(
      panel.addressField,
      "switching carried the unsaved edit onto the other card",
    ).toHaveValue("");
    await expect(
      panel.keyField,
      "switching carried the unsaved key onto the other card",
    ).toHaveValue("");

    await panel.switchButton.click();
    await expect(
      panel.addressField,
      "switching back showed the discarded edit rather than what is stored",
    ).toHaveValue(v3Address);
  });

  await test.step("a callback address edit survives a reload", async () => {
    const before = await panel.callbackField.inputValue();
    expect(before, "the callback field never took the address the server built").toContain(
      "/callback",
    );

    await panel.callbackField.fill(
      `${EDITED_CALLBACK_HOST}/api/extensions/${EXTENSION_ID}/callback`,
    );
    await panel.registerButton.click();
    // The registration answers with the address as the server now builds it, so the field settling on
    // the edited host is the write having landed.
    await expect(panel.callbackField).toHaveValue(new RegExp(`^${EDITED_CALLBACK_HOST}/`), {
      timeout: ATTEMPT_BUDGET_MS,
    });

    const reloaded = await openPanel(page, baseUrl);
    await expect(
      reloaded.callbackField,
      "the edited callback host did not survive a fresh load",
    ).toHaveValue(new RegExp(`^${EDITED_CALLBACK_HOST}/`), { timeout: ATTEMPT_BUDGET_MS });
  });

  await test.step("a save that changes only the connection does not reload", async () => {
    const after = await openPanel(page, baseUrl);
    await expect(after.addressField).toHaveValue(v3Address, { timeout: ATTEMPT_BUDGET_MS });

    // A save with nothing changed cannot even be issued, and says so rather than dimming silently.
    await expect(after.saveButton).toBeDisabled();
    await expect(after.saveButton).toHaveAccessibleName(/Nothing has changed/);

    // A trailing slash would not do: that is not an edit, so the control would stay disabled and the
    // step would assert nothing. Re-entering the key is a real write on the same generation.
    await plantMarker(page);
    await after.keyField.fill(whisparrPair.apiKey);
    await after.saveButton.click();
    await expect(page.getByText("Connection saved.", { exact: true })).toBeVisible({
      timeout: ATTEMPT_BUDGET_MS,
    });

    expect(await readMarker(page), "a save that changed no generation reloaded the page").toBe(1);
  });

  await test.step("a save that changes the generation reloads", async () => {
    const after = await openPanel(page, baseUrl);
    await after.switchButton.click();
    await after.addressField.fill(v2Address);
    await after.keyField.fill(whisparrPair.apiKey);

    await plantMarker(page);
    await after.saveButton.click();

    await expect
      .poll(() => readMarker(page), {
        message: "a save that changed the generation did not reload the page",
        timeout: ATTEMPT_BUDGET_MS,
      })
      .toBeUndefined();

    const stored = await storedSettings();
    expect(stored.selectedGeneration).toBe("v2");
    expect(stored.v2.address).toBe(v2Address);
    expect(stored.v3.address, "changing generation discarded the other card's connection").toBe(
      v3Address,
    );
  });
});

// The whole settings page against a live Cove with BOTH Whisparr generations running: the
// generation row, the callback edit, the save bar and the reload.
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
import { test as base, expect, createApiClient } from "@cove-extensions/e2e";
import { startWhisparr } from "@cove-extensions/e2e/whisparr";
import { EXTENSION_ID, SETTINGS_ROUTE } from "../../lib/contract.mjs";
import { isolatedCoveFixture } from "../../lib/whisparr-sync-fixtures.mjs";

const PANEL_PATH = "/settings/whisparr-sync";
const STATUS_PATH = "/api/v3/system/status";

// A host that resolves on the shared network, so Whisparr's own save-time connection test of the
// registered address does not refuse it - a refusal would measure that rather than the edit.
const EDITED_CALLBACK_HOST = "http://cove:5073";

// A cold container serving the extension bundle for the first time is slow rather than broken and
// raises no signal to wait on.
const ATTEMPT_BUDGET_MS = 60_000;
const ATTEMPTS = 3;

// The generation row names each option for the generation it is, and its control for the generation
// it selects.
const GENERATION_LABELS = { v3: "Whisparr v3 (Eros)", v2: "Whisparr v2" };

const test = base.extend({
  isolatedHarness: isolatedCoveFixture(),

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
    behaviorField: page.getByLabel("Replacement files"),
    testButton: page.getByRole("button", { name: "Test connection" }),
    saveButton: page.getByRole("button", { name: "Save changes" }),
    discardButton: page.getByRole("button", { name: "Discard" }),
    saveBar: saveBarIn(page),
    selectV3: page.getByRole("button", { name: `Select ${GENERATION_LABELS.v3}` }),
    selectV2: page.getByRole("button", { name: `Select ${GENERATION_LABELS.v2}` }),
    registerButton: page.getByRole("button", { name: "Register in Whisparr" }),
    // The name carries the reason the control names when it is unavailable, so it is matched from
    // the front rather than whole.
    syncButton: page.getByRole("button", { name: /^Sync library to Whisparr/ }),
  };
}

/**
 * The save bar, located by the pair of controls only it holds.
 *
 * It has no role and no fixed text of its own, and no other section of this page draws a Discard.
 * Every ancestor of the pair matches too, and each of them matches only while the bar is drawn; the
 * innermost is taken so the locator resolves to one element.
 */
function saveBarIn(page) {
  return page
    .locator("div")
    .filter({ has: page.getByRole("button", { name: "Save changes" }) })
    .filter({ has: page.getByRole("button", { name: "Discard" }) })
    .last();
}

/**
 * The option the generation row marks as the generation the form holds.
 *
 * The mark is a word inside that option. Every ancestor of it matches too, so the innermost is
 * taken: the line holding the generation's own name beside the mark.
 */
function draftedGenerationIn(page) {
  return page.locator("div").filter({ hasText: "Selected" }).last();
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

/**
 * The planted value, or undefined once the page has navigated away from the context holding it.
 *
 * A read that lands while the reload is in flight is torn down with the context it was running in,
 * and that teardown is the very event this reads for — so it answers undefined rather than throwing.
 * Any other failure still throws: a page that broke some other way must not read as a page that
 * reloaded.
 */
const readMarker = (page) =>
  page
    .evaluate((name) => window[name], MARKER)
    .catch((cause) => {
      if (/Execution context was destroyed/i.test(cause?.message ?? "")) {
        return undefined;
      }
      throw cause;
    });

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
    const read = await owner.get(SETTINGS_ROUTE);
    expect(read.status, `GET ${SETTINGS_ROUTE} answered: ${read.text.slice(0, 300)}`).toBe(200);
    return read.json;
  };

  const v3Version = await reportedVersion(whisparrPair, "v3");
  const v2Version = await reportedVersion(whisparrPair, "v2");
  const v3Address = whisparrPair.v3.internalBaseUrl;
  const v2Address = whisparrPair.v2.internalBaseUrl;

  const panel = await openPanel(page, baseUrl);

  await test.step("connecting on one generation leaves the other generation's stored values untouched", async () => {
    await panel.addressField.fill(v3Address);
    await panel.keyField.fill(whisparrPair.apiKey);
    await panel.testButton.click();
    await expect(
      page.getByText(`Connected to Whisparr ${v3Version}`, { exact: false }),
      `the panel never reported ${v3Version} for ${v3Address}`,
    ).toBeVisible({ timeout: ATTEMPT_BUDGET_MS });

    await panel.saveButton.click();
    await expect(page.getByText("Settings saved.", { exact: true })).toBeVisible({
      timeout: ATTEMPT_BUDGET_MS,
    });

    const stored = await storedSettings();
    expect(stored.selectedGeneration).toBe("v3");
    expect(stored.v3.address).toBe(v3Address);
    expect(stored.v3.keyIsSet).toBe(true);
    // The discriminating half: the other generation was never named by that save, and a page that
    // wrote one form to both would show here and nowhere else.
    expect(
      stored.v2.address,
      "saving one generation also wrote the other generation's address",
    ).toBe("");
    expect(stored.v2.keyIsSet, "saving one generation also wrote the other generation's key").toBe(
      false,
    );
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
    // The page states the recorded version for the generation the form holds and for no other, so
    // the other generation's reading is read back through the settings route.
    const stored = await storedSettings();
    expect(
      stored.v3.recordedVersion,
      "the test against the stored address recorded no version",
    ).toBe(v3Version);
    expect(
      stored.v2.recordedVersion,
      "verifying one generation recorded a version for the other",
    ).toBeNull();
  });

  await test.step("a test that reaches the other generation names the version and writes nothing", async () => {
    await panel.addressField.fill(v2Address);
    // A key typed with it: an address the form has changed is tested as a pair, because the stored
    // key belongs to a different address and the browser has no copy of it to send anyway.
    await panel.keyField.fill(whisparrPair.apiKey);
    await panel.testButton.click();

    await expect(
      page.getByText(`answered as Whisparr v2 ${v2Version}`, { exact: false }),
      `the panel did not name the ${v2Version} instance the v3 connection reached`,
    ).toBeVisible({ timeout: ATTEMPT_BUDGET_MS });
    await expect(page.getByText("Nothing was saved", { exact: false })).toBeVisible();

    const stored = await storedSettings();
    expect(stored.selectedGeneration, "a cross-generation detection changed the selection").toBe(
      "v3",
    );
    expect(stored.v2.address, "a cross-generation detection stored the other connection").toBe("");
    expect(stored.v3.address, "a cross-generation detection overwrote the tested connection").toBe(
      v3Address,
    );
  });

  await test.step("selecting the other generation reseeds the form from what is stored for it", async () => {
    // The v2 address is still in the field from the step above, entered against v3 and never saved.
    await expect(panel.addressField).toHaveValue(v2Address);
    await panel.selectV2.click();

    await expect(
      panel.addressField,
      "selecting the other generation carried the unsaved edit across",
    ).toHaveValue("");
    await expect(
      panel.keyField,
      "selecting the other generation carried the unsaved key across",
    ).toHaveValue("");

    await panel.selectV3.click();
    await expect(
      panel.addressField,
      "selecting back showed the discarded edit rather than what is stored",
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

    // This load holds what is stored and has reported no save of its own, so there is nothing to
    // offer and no bar at all. That is what a control saying it had nothing to save has become.
    await expect(
      after.saveBar,
      "a form matching what is stored still drew the save bar",
    ).toHaveCount(0);

    // A trailing slash would not do: that is not an edit, so nothing would become unsaved and the
    // step would assert nothing. Re-entering the key is a real write on the same generation.
    await plantMarker(page);
    await after.keyField.fill(whisparrPair.apiKey);
    await expect(after.saveBar).toBeVisible();
    await expect(
      page.getByText("The API key is not saved yet.", { exact: true }),
      "the bar appeared without naming the field that is unsaved",
    ).toBeVisible();

    await after.saveButton.click();
    await expect(page.getByText("Settings saved.", { exact: true })).toBeVisible({
      timeout: ATTEMPT_BUDGET_MS,
    });

    expect(await readMarker(page), "a save that changed no generation reloaded the page").toBe(1);
  });

  await test.step("a save that changes the generation reloads", async () => {
    const after = await openPanel(page, baseUrl);
    await after.selectV2.click();
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
    expect(
      stored.v3.address,
      "changing generation discarded the other generation's connection",
    ).toBe(v3Address);
  });

  await test.step("a save of the replacement behaviour alone writes neither connection", async () => {
    const after = await openPanel(page, baseUrl);
    const before = await storedSettings();
    const otherBehavior = before.upgradeBehavior === "replace" ? "add" : "replace";

    await after.behaviorField.selectOption(otherBehavior);
    await expect(
      page.getByText("The replacement-file behaviour is not saved yet.", { exact: true }),
    ).toBeVisible();
    await after.saveButton.click();
    await expect(page.getByText("Settings saved.", { exact: true })).toBeVisible({
      timeout: ATTEMPT_BUDGET_MS,
    });

    const stored = await storedSettings();
    expect(stored.upgradeBehavior, "the behaviour the save named was not stored").toBe(
      otherBehavior,
    );
    // One form holds both connections and this setting, so a save of the setting alone is where a
    // write of either stored connection alongside it would show.
    expect(stored.v3.address, "a behaviour save wrote the v3 address").toBe(before.v3.address);
    expect(stored.v2.address, "a behaviour save wrote the v2 address").toBe(before.v2.address);
    expect(stored.v3.keyIsSet, "a behaviour save wrote the v3 key").toBe(before.v3.keyIsSet);
    expect(stored.v2.keyIsSet, "a behaviour save wrote the v2 key").toBe(before.v2.keyIsSet);
  });

  await test.step("an address save with the key field blank leaves the stored key set", async () => {
    const after = await openPanel(page, baseUrl);
    const before = await storedSettings();
    expect(before.selectedGeneration).toBe("v2");
    expect(before.v2.keyIsSet, "there is no stored key for this step to keep").toBe(true);
    // The stored key is never handed back, so the field is blank on every load. Saving from here is
    // what asks for it to be kept.
    await expect(after.keyField).toHaveValue("");

    await after.addressField.fill(v3Address);
    // The bar drops its report of the last save at the first edit, so the sentence below is this
    // save's and not the one before it.
    await expect(
      page.getByText("The Whisparr address is not saved yet.", { exact: true }),
    ).toBeVisible();
    await after.saveButton.click();
    await expect(page.getByText("Settings saved.", { exact: true })).toBeVisible({
      timeout: ATTEMPT_BUDGET_MS,
    });

    const moved = await storedSettings();
    expect(moved.v2.address, "the address the save named was not stored").toBe(v3Address);
    expect(moved.v2.keyIsSet, "a blank key field cleared the stored key").toBe(true);

    // Back to the address the steps above stored, which says the same thing a second time and
    // leaves the settings as this spec found them.
    await after.addressField.fill(v2Address);
    await expect(
      page.getByText("The Whisparr address is not saved yet.", { exact: true }),
    ).toBeVisible();
    await after.saveButton.click();
    await expect(page.getByText("Settings saved.", { exact: true })).toBeVisible({
      timeout: ATTEMPT_BUDGET_MS,
    });

    const restored = await storedSettings();
    expect(restored.v2.address, "the address was not put back").toBe(v2Address);
    expect(restored.v2.keyIsSet, "a blank key field cleared the stored key").toBe(true);
  });

  await test.step("discarding an unsaved generation change puts the stored generation back", async () => {
    const after = await openPanel(page, baseUrl);
    await expect(draftedGenerationIn(page)).toContainText(GENERATION_LABELS.v2);

    await after.selectV3.click();
    await expect(draftedGenerationIn(page)).toContainText(GENERATION_LABELS.v3);
    await expect(after.addressField).toHaveValue(v3Address);
    // The one thing the control that used to announce a switch never did: say what is outstanding.
    await expect(
      page.getByText(
        "The Whisparr generation is not saved yet. Saving changes the generation Cove uses and reloads the page.",
        { exact: true },
      ),
      "the bar did not name the generation as the unsaved change",
    ).toBeVisible();

    await after.discardButton.click();
    await expect(
      draftedGenerationIn(page),
      "discarding left the row on the generation that was never saved",
    ).toContainText(GENERATION_LABELS.v2);
    await expect(after.addressField).toHaveValue(v2Address);
    await expect(after.saveBar).toHaveCount(0);
  });

  await test.step("the page's last control is reachable at full scroll while the bar is shown", async () => {
    const after = await openPanel(page, baseUrl);
    await after.keyField.fill(whisparrPair.apiKey);
    await expect(after.saveBar).toBeVisible();

    // Every scrollable ancestor to its end. The bar is fixed to the foot of the viewport, so the
    // foot of the page is the one place it can cover something.
    await after.syncButton.evaluate((el) => {
      for (let node = el.parentElement; node !== null; node = node.parentElement) {
        if (node.scrollHeight > node.clientHeight) node.scrollTop = node.scrollHeight;
      }
      const root = document.scrollingElement;
      if (root !== null) root.scrollTop = root.scrollHeight;
    });

    // Polled, because a page that scrolls smoothly is still moving when the scroll is asked for.
    // What is over the control is read from the document itself: the bar is drawn above everything
    // on the page, so a box that overlaps it would take the press.
    await expect
      .poll(
        () =>
          after.syncButton.evaluate((el) => {
            const box = el.getBoundingClientRect();
            const over = document.elementFromPoint(
              box.left + box.width / 2,
              box.top + box.height / 2,
            );
            return over === el || el.contains(over) ? "the control" : (over?.textContent ?? "");
          }),
        {
          message: "something else took the middle of the page's last control at full scroll",
          timeout: ATTEMPT_BUDGET_MS,
        },
      )
      .toBe("the control");

    // Nothing here was saved, and discarding leaves the settings as the steps above left them.
    await after.discardButton.click();
    await expect(after.saveBar).toHaveCount(0);
  });
});

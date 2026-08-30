// The API key is write-only against the RUNNING host, not only in the design.
//
// Two routes are read, and the second is the one that matters. The extension's own settings response
// is the surface this phase controls; Cove's bulk `GET /api/extensions/{id}/data` is the route Phase
// 51-01 measured returning an extension's stored values whole, to any caller its permission filter
// admits. A key kept out of the first and left in the second would still be readable, so both are
// asserted here.
//
// Each read is paired with a control that the answer described this extension at all: a route that
// returned nothing would satisfy a "does not contain the key" assertion exactly as a route that
// correctly withholds it does.
//
// Its own instance: this writes settings, which are instance-global.
import {
  test as base,
  expect,
  createApiClient,
  isolatedHarnessFixture,
} from "@cove-extensions/e2e";
import { WHISPARR_SYNC_EXTENSION } from "../lib/whisparr-sync-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const SETTINGS_PATH = `/api/extensions/${EXTENSION_ID}/settings`;
const DATA_PATH = `/api/extensions/${EXTENSION_ID}/data`;

// Synthetic and authorises nothing: no instance is reached in this spec. Distinctive enough that a
// substring search for it cannot match anything else the responses carry.
const SAVED_KEY = "e2ewriteonly7c41b9a6d2f80e35a1c4";
const SAVED_ADDRESS = "http://whisparr-v3-not-started:6969";

const test = base.extend({
  isolatedHarness: isolatedHarnessFixture(WHISPARR_SYNC_EXTENSION),
});

test("a saved key reaches neither the settings response nor the host's bulk data route", async ({
  isolatedHarness,
}) => {
  const owner = createApiClient(
    () => isolatedHarness.baseUrl,
    () => isolatedHarness.token,
  );

  const saved = await owner.put(SETTINGS_PATH, {
    selectedGeneration: "v3",
    v3: { address: SAVED_ADDRESS, keyWrite: "replace", apiKey: SAVED_KEY },
    v2: null,
  });

  expect(saved.status, `PUT ${SETTINGS_PATH} answered: ${saved.text}`).toBe(200);
  expect(saved.json?.v3?.keyIsSet, "the save did not store the key at all").toBe(true);
  expect(saved.text).not.toContain(SAVED_KEY);

  const read = await owner.get(SETTINGS_PATH);

  expect(read.status, `GET ${SETTINGS_PATH} answered: ${read.text}`).toBe(200);
  expect(read.json?.v3?.address).toBe(SAVED_ADDRESS);
  expect(read.json?.v3?.keyIsSet).toBe(true);
  expect(read.text).not.toContain(SAVED_KEY);

  const bulk = await owner.get(DATA_PATH);

  expect(bulk.status, `GET ${DATA_PATH} answered: ${bulk.text}`).toBe(200);
  // The discriminating control: the bulk route DID return this extension's stored values, so its
  // silence about the key is about the key rather than about an empty answer.
  expect(
    bulk.text,
    "the bulk data route returned nothing of this extension, so it is no evidence about the key",
  ).toContain(SAVED_ADDRESS);
  expect(bulk.text).not.toContain(SAVED_KEY);
});

test("a save carrying a blank key keeps the stored one", async ({ isolatedHarness }) => {
  const owner = createApiClient(
    () => isolatedHarness.baseUrl,
    () => isolatedHarness.token,
  );

  const stored = await owner.put(SETTINGS_PATH, {
    selectedGeneration: "v3",
    v3: { address: SAVED_ADDRESS, keyWrite: "replace", apiKey: SAVED_KEY },
    v2: null,
  });
  expect(stored.json?.v3?.keyIsSet).toBe(true);

  // The page cannot resubmit a key it was never given back, so this is what every later save looks
  // like unless the operator types a new one.
  const resaved = await owner.put(SETTINGS_PATH, {
    selectedGeneration: "v3",
    v3: { address: `${SAVED_ADDRESS}/`, keyWrite: "replace", apiKey: "" },
    v2: null,
  });

  expect(resaved.status, `PUT ${SETTINGS_PATH} answered: ${resaved.text}`).toBe(200);
  expect(resaved.json?.v3?.keyIsSet, "a blank key removed the stored one").toBe(true);

  const cleared = await owner.put(SETTINGS_PATH, {
    selectedGeneration: "v3",
    v3: { address: SAVED_ADDRESS, keyWrite: "clear", apiKey: null },
    v2: null,
  });

  expect(cleared.status, `PUT ${SETTINGS_PATH} answered: ${cleared.text}`).toBe(200);
  expect(cleared.json?.v3?.keyIsSet, "an explicit clear left the key in place").toBe(false);
});

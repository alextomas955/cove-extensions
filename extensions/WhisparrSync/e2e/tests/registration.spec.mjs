// Registering the import callback, end to end, against BOTH live Whisparr generations.
//
// Every assertion here is on the EFFECT, read back through Whisparr's own API. None is on the status
// of the registration request. That distinction is the whole point: a 202 from an update says the
// request was well formed and says nothing about where the notification now points, and this has
// shipped broken three ways at once before, all silent, all invisible to a test that checked the
// request was accepted.
//
// Registering twice is what proves idempotency, and it is proven by COUNTING what Whisparr holds
// rather than by trusting the extension's own report of what it did.
import { test as base, expect, createApiClient } from "@cove-extensions/e2e";
import { WHISPARR_SYNC_EXTENSION } from "../lib/whisparr-sync-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";
const SETTINGS_PATH = `/api/extensions/${EXTENSION_ID}/settings`;
const REGISTER_PATH = `/api/extensions/${EXTENSION_ID}/callback/register`;
const STATUS_PATH = `/api/extensions/${EXTENSION_ID}/callback/status`;
const NOTIFICATION_PATH = "/api/v3/notification";

// Transcribed by hand from the port's own frozen constant. Written out rather than imported, so a
// rename on either side has to be made in both places.
const REGISTRATION_NAME = "Cove Whisparr Sync";

// The first address is Cove's own alias on the shared network. The second is the SAME instance by
// its in-network address, read off the container, so the two are different spellings a user could
// plausibly type and BOTH resolve.
//
// Both have to resolve. Whisparr tests a Webhook connection when it saves one and answers 500 when
// the address does not resolve, so an unreachable second address would measure that refusal rather
// than the move — which is what a first run of this spec did.
const FIRST_HOST = "http://cove:5073";
const COVE_PORT = 5073;

const test = base.extend({
  extension: [WHISPARR_SYNC_EXTENSION, { option: true }],
});

test.use({ whisparrGenerations: ["v3", "v2"] });

const coveClient = (baseUrl, token) =>
  createApiClient(
    () => baseUrl,
    () => token,
  );

/** Every notification the instance holds under this product's name, read through its own API. */
async function registrationsOn(whisparr, generation) {
  const listed = await whisparr.apiFor(generation).get(NOTIFICATION_PATH);
  expect(
    listed.status,
    `${generation} did not answer ${NOTIFICATION_PATH}: ${listed.text.slice(0, 300)}`,
  ).toBe(200);
  return (listed.json ?? []).filter((entry) => entry.name === REGISTRATION_NAME);
}

/** The address one listed notification posts to, read out of its own fields array. */
const addressOf = (entry) => entry.fields?.find((field) => field.name === "url")?.value ?? null;

/**
 * A second spelling of Cove's own address that also resolves on the shared network.
 *
 * Read off the container rather than written down: the address depends on the network the run
 * created, and a literal would be right until the first time it was not.
 */
async function secondCoveHost(harness) {
  const { output } = await harness.exec(["sh", "-c", "hostname -i"]);
  const [address] = output.trim().split(/\s+/).filter(Boolean);
  expect(address, `the Cove container reported no in-network address: ${output}`).toMatch(
    /^\d+\.\d+\.\d+\.\d+$/,
  );
  return `http://${address}:${COVE_PORT}`;
}

/** Points the extension at one generation's instance and stores its key. */
async function configure(api, whisparr, generation) {
  const half = {
    address: whisparr[generation].internalBaseUrl,
    keyWrite: "replace",
    apiKey: whisparr.apiKey,
  };
  const saved = await api.put(SETTINGS_PATH, {
    selectedGeneration: generation,
    v3: generation === "v3" ? half : null,
    v2: generation === "v2" ? half : null,
  });
  expect(saved.status, `saving settings failed: ${saved.text.slice(0, 300)}`).toBe(200);
}

for (const generation of ["v3", "v2"]) {
  test(`registering twice on ${generation} leaves one notification, moved to the second address`, async ({
    baseUrl,
    harness,
    whisparr,
  }) => {
    // Two container-crossing registrations plus an install restart. The default per-test budget
    // covers neither.
    test.setTimeout(600_000);

    const api = coveClient(baseUrl, harness.token);
    const secondHost = await secondCoveHost(harness);
    await configure(api, whisparr, generation);

    expect(
      await registrationsOn(whisparr, generation),
      `${generation} already held a notification named "${REGISTRATION_NAME}" before anything registered one`,
    ).toHaveLength(0);

    // ── The status before anything has looked ────────────────────────────────────────────────────
    //
    // Three states, and this is the third. "Not checked yet" is not "not registered": the instance
    // has not been asked.
    const before = await api.get(STATUS_PATH);
    expect(before.status, before.text.slice(0, 300)).toBe(200);
    expect(before.json.status).toBe("notCheckedYet");

    // Nothing is registered, so Whisparr holds no address to deliver to and no event can have
    // arrived. "No event yet" is a state of its own, distinct from not being registered.
    expect(before.json.lastEventSecretPosition ?? null).toBeNull();

    // ── First registration ───────────────────────────────────────────────────────────────────────
    const first = await api.post(REGISTER_PATH, {
      callbackAddress: `${FIRST_HOST}/api/extensions/${EXTENSION_ID}/callback`,
    });

    // The status is never a subject on its own here. It is asserted together with what the extension
    // reported about the EFFECT, and every claim that the registration worked is made below against
    // Whisparr's own list.
    expect(
      { status: first.status, refusal: first.json?.refusal ?? null },
      first.text.slice(0, 300),
    ).toEqual({ status: 200, refusal: null });

    const afterFirst = await registrationsOn(whisparr, generation);
    expect(
      afterFirst,
      `${generation} does not hold exactly one notification named "${REGISTRATION_NAME}" after the first registration`,
    ).toHaveLength(1);
    expect(addressOf(afterFirst[0])).toBe(`${FIRST_HOST}/api/extensions/${EXTENSION_ID}/callback`);

    // The registered address carries no secret on either generation, because both can carry one off
    // the address — one in a custom header, the other as Basic auth.
    expect(addressOf(afterFirst[0])).not.toContain("?");
    expect(first.json.secretTravelsOutOfBand).toBe(true);
    expect(first.json.copyableAddress).toContain("?s=");

    // ── Second registration, at a different address ──────────────────────────────────────────────
    const second = await api.post(REGISTER_PATH, {
      callbackAddress: `${secondHost}/api/extensions/${EXTENSION_ID}/callback`,
    });
    expect(
      { status: second.status, refusal: second.json?.refusal ?? null },
      second.text.slice(0, 300),
    ).toEqual({ status: 200, refusal: null });

    const afterSecond = await registrationsOn(whisparr, generation);
    expect(
      afterSecond,
      `registering twice left ${afterSecond.length} notifications named "${REGISTRATION_NAME}" on ${generation}`,
    ).toHaveLength(1);
    expect(
      addressOf(afterSecond[0]),
      "the second registration was accepted and the notification still points at the first address",
    ).toBe(`${secondHost}/api/extensions/${EXTENSION_ID}/callback`);
    expect(
      afterSecond[0].id,
      "the second registration replaced the entry rather than updating it in place",
    ).toBe(afterFirst[0].id);

    // ── The status after ─────────────────────────────────────────────────────────────────────────
    const after = await api.get(STATUS_PATH);
    expect(after.status, after.text.slice(0, 300)).toBe(200);
    expect(after.json.status).toBe("registered");
    expect(after.json.registeredAddress).toBe(
      `${secondHost}/api/extensions/${EXTENSION_ID}/callback`,
    );

    // The edit survives a refresh because it is stored as the callback host rather than held in the
    // page: this read is a fresh request that supplied no address.
    expect(after.json.copyableAddress.startsWith(secondHost)).toBe(true);

    // The secret position is not asserted here: the registered address resolves on the shared
    // network, so a delivery can land at any point once the registration returns, and a delivery's
    // position survives the registration that was in flight. Its absence is asserted above, before
    // anything is registered.
  });
}

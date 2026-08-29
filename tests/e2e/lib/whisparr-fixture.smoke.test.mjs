// A `node --test` smoke rather than a Playwright spec. playwright.config.mjs derives its projects
// from the extension catalog, and this harness's own `tests/` directory is deliberately not a
// catalog entry, so a spec placed there is collected by no project and silently never runs. This
// needs no browser either.
import { test } from "node:test";
import assert from "node:assert/strict";
import { startHarness } from "./harness.mjs";
import { libraryRootsContaining, startWhisparr } from "./whisparr-fixture.mjs";
import { createApiClient } from "./apiClient.mjs";

const STATUS_PATH = "/api/v3/system/status";
const HISTORY_PATH = "/api/v3/history?page=1&pageSize=10";
const COVE_CONFIG_PATH = "/api/system/config";

// Not the fixture's key, and not a shape any instance would mint for itself.
const WRONG_API_KEY = "ffffffffffffffffffffffffffffffff";

const EXPECTED_VERSION_PREFIX = { v3: "3.", v2: "2." };

// Stated here rather than read off the fixture: an expectation taken from the module it checks
// agrees with that module forever and reports nothing.
const SEEDED_HISTORY_ROWS = 3;

// The history API's own vocabulary, which is NOT the webhook's — the same event is camelCase on one
// surface and PascalCase on the other, so a parser written against either matches nothing on the
// other without failing anywhere.
const CAMEL_CASE = /^[a-z][A-Za-z]*$/;

// The two first-run path variants, one per generation so a single bring-up carries both. Which
// generation holds which is immaterial — containment is decided on the reported strings.
const ROOT_FOLDERS = { v3: "/data", v2: "/media/whisparr" };

// How many of the library roots COVE declares must contain each of those: the aligned one falls
// under exactly one, the mismatched one under none. The second is the condition that leaves a host
// unable to resolve a path an instance reported.
const EXPECTED_CONTAINING_ROOTS = { v3: 1, v2: 0 };

test("both Whisparr generations answer their own API beside the harness Cove", async (t) => {
  const beforeSeed = new Date(Date.now() - 3_600_000).toISOString();
  const harness = await startHarness();
  try {
    const coveRoots = await coveLibraryRoots(harness);
    const [network] = harness.container.getNetworkNames();
    const whisparr = await startWhisparr({
      network,
      seedHistory: { count: SEEDED_HISTORY_ROWS },
      rootFolder: ROOT_FOLDERS,
    });
    // Nested inside the harness's own teardown: a compose down removes the project network, and the
    // daemon refuses to remove a network that still has an attached endpoint.
    try {
      for (const generation of whisparr.generations) {
        await t.test(generation, async () => {
          const status = await whisparr.apiFor(generation).get(STATUS_PATH);

          assert.equal(
            status.status,
            200,
            `expected 200 from ${STATUS_PATH}, body: ${status.text}`,
          );
          assert.ok(
            status.contentType.startsWith("application/json"),
            `expected a JSON content type, got "${status.contentType}"`,
          );
          assert.ok(
            String(status.json?.version).startsWith(EXPECTED_VERSION_PREFIX[generation]),
            `expected a ${generation} version, got "${status.json?.version}"`,
          );

          // The refusals are what make the 200 above mean something: without them a silently open
          // API would pass just as well as a seeded key.
          const baseUrl = () => whisparr[generation].baseUrl;
          const anonymous = await createApiClient(baseUrl).get(STATUS_PATH);
          assert.equal(anonymous.status, 401, "expected 401 with no key");

          const wrongKey = await createApiClient(baseUrl, undefined, {
            headers: { "X-Api-Key": WRONG_API_KEY },
          }).get(STATUS_PATH);
          assert.equal(wrongKey.status, 401, "expected 401 with a wrong key");

          await assertSeededHistory(whisparr.apiFor(generation), beforeSeed);

          // Judged against the roots COVE reports and the root WHISPARR reports, so neither side of
          // the comparison is a value this test supplied.
          assert.equal(
            libraryRootsContaining(whisparr[generation].rootFolder, coveRoots).length,
            EXPECTED_CONTAINING_ROOTS[generation],
            `Cove declares ${coveRoots.join(", ")}, and ${generation} reports ${whisparr[generation].rootFolder}`,
          );
        });
      }
    } finally {
      await whisparr.stop();
    }
  } finally {
    await harness.stop();
  }
});

/**
 * The instance has an import past, and it is readable through the two routes a first-pass claim
 * would be built on. An empty history is not a confirmed zero here: it is what a failed seed looks
 * like, which is why the count is asserted rather than the status alone.
 */
async function assertSeededHistory(api, since) {
  const history = await api.get(HISTORY_PATH);
  assert.equal(history.status, 200, `expected 200 from the history API, body: ${history.text}`);
  assert.ok(
    history.contentType.startsWith("application/json"),
    `expected a JSON content type, got "${history.contentType}"`,
  );
  assert.equal(history.json?.totalRecords, SEEDED_HISTORY_ROWS);

  for (const record of history.json.records) {
    assert.ok(
      record.sourceTitle,
      `a seeded record carried no sourceTitle: ${JSON.stringify(record)}`,
    );
    assert.match(record.eventType, CAMEL_CASE);
  }

  const watermark = await api.get(`/api/v3/history/since?date=${encodeURIComponent(since)}`);
  assert.equal(
    watermark.status,
    200,
    `expected 200 from the watermark read, body: ${watermark.text}`,
  );
  assert.ok(Array.isArray(watermark.json), `expected an array, got "${watermark.contentType}"`);
  assert.equal(watermark.json.length, SEEDED_HISTORY_ROWS);
}

/**
 * The library roots Cove itself declares. Asking the host rather than restating the harness's own
 * compose environment is the point: a check built from the value this suite supplied agrees with
 * itself however wrong the running configuration is.
 *
 * Only the paths are taken from that response, and none of it is logged: for a principal that may
 * write system settings it also carries provider API keys in the clear.
 */
async function coveLibraryRoots(harness) {
  await harness.bootstrapOwner();
  const config = await createApiClient(
    () => harness.baseUrl,
    () => harness.token,
  ).get(COVE_CONFIG_PATH);
  assert.equal(config.status, 200, `expected 200 from ${COVE_CONFIG_PATH}`);
  const roots = config.json?.covePaths?.map((entry) => entry.path) ?? [];
  assert.ok(roots.length > 0, `${COVE_CONFIG_PATH} declared no library root`);
  return roots;
}

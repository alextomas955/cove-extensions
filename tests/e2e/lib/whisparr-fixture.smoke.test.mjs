// A `node --test` smoke rather than a Playwright spec. playwright.config.mjs derives its projects
// from the extension catalog, and this harness's own `tests/` directory is deliberately not a
// catalog entry, so a spec placed there is collected by no project and silently never runs. This
// needs no browser either.
import { test } from "node:test";
import assert from "node:assert/strict";
import { startHarness } from "./harness.mjs";
import { startWhisparr } from "./whisparr-fixture.mjs";
import { createApiClient } from "./apiClient.mjs";

const STATUS_PATH = "/api/v3/system/status";

// Not the fixture's key, and not a shape any instance would mint for itself.
const WRONG_API_KEY = "ffffffffffffffffffffffffffffffff";

const EXPECTED_VERSION_PREFIX = { v3: "3.", v2: "2." };

test("both Whisparr generations answer their own API beside the harness Cove", async (t) => {
  const harness = await startHarness();
  try {
    const [network] = harness.container.getNetworkNames();
    const whisparr = await startWhisparr({ network });
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
        });
      }
    } finally {
      await whisparr.stop();
    }
  } finally {
    await harness.stop();
  }
});

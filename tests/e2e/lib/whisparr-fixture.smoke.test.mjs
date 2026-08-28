// A `node --test` smoke rather than a Playwright spec. playwright.config.mjs derives its projects
// from the extension catalog, and this harness's own `tests/` directory is deliberately not a
// catalog entry, so a spec placed there is collected by no project and silently never runs. This
// needs no browser either.
import { test } from "node:test";
import assert from "node:assert/strict";
import { startHarness } from "./harness.mjs";
import { startWhisparr } from "./whisparr-fixture.mjs";

const STATUS_PATH = "/api/v3/system/status";

test("a seeded-key Whisparr v3 answers its own API beside the harness Cove", async () => {
  const harness = await startHarness();
  try {
    const [network] = harness.container.getNetworkNames();
    const whisparr = await startWhisparr({ network, generations: ["v3"] });
    // Nested inside the harness's own teardown: a compose down removes the project network, and the
    // daemon refuses to remove a network with an attached endpoint.
    try {
      const api = whisparr.apiFor("v3");
      const status = await api.get(STATUS_PATH);

      assert.equal(status.status, 200, `expected 200 from ${STATUS_PATH}, body: ${status.text}`);
      assert.ok(
        status.contentType.startsWith("application/json"),
        `expected a JSON content type, got "${status.contentType}"`,
      );
      assert.ok(
        String(status.json?.version).startsWith("3."),
        `expected a v3 version, got "${status.json?.version}"`,
      );
    } finally {
      await whisparr.stop();
    }
  } finally {
    await harness.stop();
  }
});

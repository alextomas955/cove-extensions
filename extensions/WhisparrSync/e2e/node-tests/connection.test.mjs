// Offline correctness spec (node:test), run by `node --test`. Against a REAL Whisparr v3 (Eros)
// container: Test-connection classifies the connection as a v3 build with a non-empty instance name.
// There is no root-folder or quality-profile list assertion here, because the extension
// exposes neither as an endpoint — it resolves both from the instance per add, and config-guard.test
// .mjs proves that live by reading the created row's own root and profile back out of Whisparr.
// This leg needs a real Whisparr but no metadata resolution, so it is the cheapest real-state proof —
// no SkyHook stub entry is exercised. The Whisparr API key is read out-of-band from the running
// container by the harness; nothing secret is read or asserted here.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { startWhisparrSyncHarness, EXTENSION_ID } from "../lib/setup.mjs";

let ctx;

before(async () => {
  ctx = await startWhisparrSyncHarness({ version: "v3" });
}, { timeout: 600_000 });

after(async () => {
  await ctx?.stop();
}, { timeout: 120_000 });

test("Test-connection detects a v3 (Eros) build with a non-empty instance name", async () => {
  const { api, whisparr } = ctx;
  const res = await api.post(`/api/extensions/${EXTENSION_ID}/test-connection`, {
    baseUrl: whisparr.baseUrlFromCove,
    apiKey: whisparr.apiKey,
  });

  assert.equal(res.status, 200, `test-connection HTTP status (body: ${res.text})`);
  assert.equal(res.json.result, "success");
  // The detected version is a real Whisparr v3 build string (major 3 / Eros), classified from the
  // parsed version — never from the 200 status, since a v2 instance also answers /api/v3.
  assert.match(String(res.json.version ?? ""), /^3\./, "detected version begins with the v3 major");
  assert.notEqual(String(res.json.instanceName ?? ""), "", "instance name is non-empty");
});

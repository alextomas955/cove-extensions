// Offline correctness spec (node:test), run by `node --test`. Against a REAL Whisparr v3 (Eros)
// container: Test-connection classifies the connection as a v3 build with a non-empty instance name
// (CONN-03), and the root-folder + quality-profile lists populate from the live instance (CONN-05).
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

test("root folders and quality profiles populate from the live instance", async () => {
  const { api, whisparr } = ctx;
  const creds = { BaseUrl: whisparr.baseUrlFromCove, ApiKey: whisparr.apiKey };

  const roots = await api.post(`/api/extensions/${EXTENSION_ID}/rootfolders`, creds);
  assert.equal(roots.status, 200);
  assert.ok(Array.isArray(roots.json), "root folders is an array");
  // The container helper provisioned a real root folder, so the list is non-empty (CONN-05).
  assert.ok(roots.json.length > 0, "at least one root folder");

  const profiles = await api.post(`/api/extensions/${EXTENSION_ID}/qualityprofiles`, creds);
  assert.equal(profiles.status, 200);
  assert.ok(Array.isArray(profiles.json), "quality profiles is an array");
  assert.ok(profiles.json.length > 0, "at least one quality profile");
});

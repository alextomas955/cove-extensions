/**
 * Behavior contract for the pure webhook logic. The runner (check-webhook-logic.mjs) compiles
 * webhookLogic.ts and passes the compiled module URL via WEBHOOK_LOGIC_MODULE.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.WEBHOOK_LOGIC_MODULE);
const { webhookUrlFromServer, registrationForVersion, resolveWebhookStatus, EMPTY_WEBHOOK } = mod;

const view = (connections) => ({ url: "", registered: false, connections });

test("webhookUrlFromServer: reads the camelCase WebhookUrlResponse shape", () => {
  const v = webhookUrlFromServer({ url: "http://host.docker.internal:5073/hook", registered: true });
  assert.equal(v.url, "http://host.docker.internal:5073/hook");
  assert.equal(v.registered, true);
});

test("webhookUrlFromServer: the old PascalCase shape is no longer read (camel-only)", () => {
  const v = webhookUrlFromServer({ Url: "http://localhost:5073/hook", Registered: true });
  assert.equal(v.url, "");
  assert.equal(v.registered, false);
});

test("webhookUrlFromServer: malformed/absent payload is the empty default", () => {
  assert.deepEqual(webhookUrlFromServer(null), EMPTY_WEBHOOK);
  assert.deepEqual(webhookUrlFromServer("nope"), EMPTY_WEBHOOK);
  assert.deepEqual(webhookUrlFromServer(undefined), EMPTY_WEBHOOK);
  assert.deepEqual(webhookUrlFromServer({}), EMPTY_WEBHOOK);
});

test("webhookUrlFromServer: wrong-typed fields fall back to url='' and registered=false", () => {
  const v = webhookUrlFromServer({ url: 42, registered: "yes" });
  assert.equal(v.url, "");
  assert.equal(v.registered, false);
});

test("webhookUrlFromServer: reads the per-connection list", () => {
  const v = webhookUrlFromServer({
    url: "http://cove:5073/hook",
    registered: false,
    connections: [
      { version: "v3", baseUrl: "http://w3:6971", registered: true },
      { version: "v2", baseUrl: "http://w2:6972", registered: false },
    ],
  });
  assert.equal(v.connections.length, 2);
  assert.deepEqual(v.connections[0], { version: "v3", baseUrl: "http://w3:6971", registered: true });
});

test("webhookUrlFromServer: a malformed ENTRY is skipped, never bound", () => {
  const v = webhookUrlFromServer({
    url: "",
    registered: false,
    connections: [
      { version: "v3", baseUrl: "http://w3:6971", registered: true },
      { version: 3, baseUrl: "http://w2:6972", registered: false }, // non-string version
      { version: "v2", baseUrl: null, registered: false }, // non-string baseUrl
      { version: "v2", baseUrl: "http://w2:6972", registered: "yes" }, // non-boolean flag
      null,
      "nope",
    ],
  });
  assert.equal(v.connections.length, 1);
  assert.equal(v.connections[0].version, "v3");
});

test("webhookUrlFromServer: a malformed LIST yields an empty list and never throws", () => {
  assert.deepEqual(webhookUrlFromServer({ url: "", registered: false, connections: "nope" }).connections, []);
  assert.deepEqual(webhookUrlFromServer({ url: "", registered: false, connections: 42 }).connections, []);
  assert.deepEqual(webhookUrlFromServer({ url: "", registered: false }).connections, []);
  assert.deepEqual(webhookUrlFromServer(null).connections, []);
});

test("registrationForVersion: the answer is the SELECTED version's own, not the other instance's", () => {
  // The live defect this removes: the connector exists on v3 while Cove is pointed at v2.
  const v = view([
    { version: "v3", baseUrl: "http://w3:6971", registered: true },
    { version: "v2", baseUrl: "http://w2:6972", registered: false },
  ]);
  assert.equal(registrationForVersion(v, "v3"), "registered");
  assert.equal(registrationForVersion(v, "v2"), "notRegistered");
});

test("registrationForVersion: a version with no entry is notChecked, never notRegistered", () => {
  const v = view([{ version: "v3", baseUrl: "http://w3:6971", registered: true }]);
  assert.equal(registrationForVersion(v, "v2"), "notChecked");
  assert.equal(registrationForVersion(EMPTY_WEBHOOK, "v3"), "notChecked");
});

test("registrationForVersion: the version key matches case-insensitively", () => {
  const v = view([{ version: "V3", baseUrl: "http://w3:6971", registered: true }]);
  assert.equal(registrationForVersion(v, "v3"), "registered");
});

test("resolveWebhookStatus: an authoritative registered answer reads registered even with no events", () => {
  const s = resolveWebhookStatus("registered", null);
  assert.equal(s.state, "registered");
  assert.equal(s.hasEvents, false);
});

test("resolveWebhookStatus: a stale event never makes an absent connector read registered", () => {
  // The connector is authoritative: a past import-log event against a deleted connection must NOT report
  // registered — it only enriches the status text when the connection actually exists.
  const s = resolveWebhookStatus("notRegistered", 123);
  assert.equal(s.state, "notRegistered");
  assert.equal(s.hasEvents, true);
});

test("resolveWebhookStatus: a stale event never upgrades an UNCHECKED instance either", () => {
  const s = resolveWebhookStatus("notChecked", 123);
  assert.equal(s.state, "notChecked");
  assert.equal(s.hasEvents, true);
});

test("resolveWebhookStatus: an absent event never downgrades a registered answer", () => {
  assert.equal(resolveWebhookStatus("registered", 456).state, "registered");
  assert.equal(resolveWebhookStatus("registered", null).state, "registered");
});

test("resolveWebhookStatus: neither an answer nor an event reads not-registered", () => {
  const s = resolveWebhookStatus("notRegistered", null);
  assert.equal(s.state, "notRegistered");
  assert.equal(s.hasEvents, false);
});

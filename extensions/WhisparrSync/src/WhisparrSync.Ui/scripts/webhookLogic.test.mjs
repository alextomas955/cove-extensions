/**
 * Behavior contract for the pure webhook logic. The runner (check-webhook-logic.mjs) compiles
 * webhookLogic.ts and passes the compiled module URL via WEBHOOK_LOGIC_MODULE.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.WEBHOOK_LOGIC_MODULE);
const { webhookUrlFromServer, resolveWebhookStatus, EMPTY_WEBHOOK } = mod;

test("webhookUrlFromServer: reads the PascalCase WebhookUrlResponse shape", () => {
  const v = webhookUrlFromServer({ Url: "http://host.docker.internal:5073/hook", Registered: true });
  assert.equal(v.url, "http://host.docker.internal:5073/hook");
  assert.equal(v.registered, true);
});

test("webhookUrlFromServer: tolerates the loose lowercase shape", () => {
  const v = webhookUrlFromServer({ url: "http://localhost:5073/hook", registered: false });
  assert.equal(v.url, "http://localhost:5073/hook");
  assert.equal(v.registered, false);
});

test("webhookUrlFromServer: malformed/absent payload is the empty default", () => {
  assert.deepEqual(webhookUrlFromServer(null), EMPTY_WEBHOOK);
  assert.deepEqual(webhookUrlFromServer("nope"), EMPTY_WEBHOOK);
  assert.deepEqual(webhookUrlFromServer(undefined), EMPTY_WEBHOOK);
  assert.deepEqual(webhookUrlFromServer({}), EMPTY_WEBHOOK);
});

test("webhookUrlFromServer: wrong-typed fields fall back to url='' and registered=false", () => {
  const v = webhookUrlFromServer({ Url: 42, Registered: "yes" });
  assert.equal(v.url, "");
  assert.equal(v.registered, false);
});

test("resolveWebhookStatus: an authoritative registered flag reads registered even with no events", () => {
  const s = resolveWebhookStatus(true, null);
  assert.equal(s.isRegistered, true);
  assert.equal(s.hasEvents, false);
});

test("resolveWebhookStatus: a stale event never makes an absent connector read registered", () => {
  // The connector is authoritative: a past import-log event with registered:false (connection deleted) must
  // NOT report registered — it only enriches the status text when the connection actually exists.
  const s = resolveWebhookStatus(false, 123);
  assert.equal(s.isRegistered, false);
  assert.equal(s.hasEvents, true);
});

test("resolveWebhookStatus: an absent event never downgrades a true registered flag", () => {
  const s = resolveWebhookStatus(true, 456);
  assert.equal(s.isRegistered, true);
  assert.equal(s.hasEvents, true);

  // registered:true with no event is still registered — the event only enriches, never overrides.
  assert.equal(resolveWebhookStatus(true, null).isRegistered, true);
});

test("resolveWebhookStatus: neither a registered flag nor an event reads not-registered", () => {
  const s = resolveWebhookStatus(false, null);
  assert.equal(s.isRegistered, false);
  assert.equal(s.hasEvents, false);
});

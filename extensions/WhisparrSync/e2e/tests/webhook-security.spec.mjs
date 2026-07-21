// Hermetic (Cove-only, no Whisparr container): the inbound webhook (SEC-01) rejects an unsigned / unknown /
// Test event and ingests nothing. This is the regression proof for T-15-03 — the shared-secret token is the
// ONLY auth on the anonymous /webhook route, and with no secret configured every post is fail-closed 401.
import { test, expect } from "../lib/whisparrsync-fixtures.mjs";

const EXTENSION_ID = "com.alextomas955.whisparrsync";

test("the webhook rejects unsigned / unknown / Test events and ingests nothing", async ({
  baseUrl,
  api,
}) => {
  const webhookUrl = `${baseUrl}/api/extensions/${EXTENSION_ID}/webhook`;

  // Raw fetch so we can control the X-Cove-Token header (the shared `api` helper only sets Content-Type).
  async function postWebhook(headers, body) {
    const res = await fetch(webhookUrl, {
      method: "POST",
      headers: { "Content-Type": "application/json", ...headers },
      body: JSON.stringify(body),
    });
    return res.status;
  }

  // (a) No token at all.
  expect(await postWebhook({}, { eventType: "Download" })).not.toBe(200);
  // (b) An unknown / garbage token.
  expect(await postWebhook({ "X-Cove-Token": "totally-bogus-token" }, { eventType: "Download" })).not.toBe(200);
  // (c) A well-formed body whose eventType is Test, still with a bogus token — rejected on the token first.
  expect(await postWebhook({ "X-Cove-Token": "totally-bogus-token" }, { eventType: "Test" })).not.toBe(200);

  // Nothing was ingested — the extension's own import log stays empty.
  const log = await api.get(`/api/extensions/${EXTENSION_ID}/import-log`);
  expect(log.status).toBe(200);
  expect(Array.isArray(log.json.entries) ? log.json.entries.length : -1).toBe(0);
});

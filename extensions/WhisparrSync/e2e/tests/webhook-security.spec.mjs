// Hermetic (Cove-only, no Whisparr container): the inbound webhook rejects an unsigned / unknown / Test
// event and ingests nothing. The shared-secret token is the ONLY auth on the anonymous /webhook route, and
// with no secret configured every post is fail-closed 401.
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

  // Nothing was ingested. /import-log carries bounded aggregates rather than a per-attempt journal, so
  // "nothing happened" is the absence of an import outcome — never an empty entries array, which the
  // endpoint stopped returning when the store was capped and which this asserted against for so long
  // that it read -1 rather than 0.
  const log = await api.get(`/api/extensions/${EXTENSION_ID}/import-log`);
  expect(log.status).toBe(200);
  const importLeg = (log.json.pipelineHealth ?? []).find((d) => d.dependency === "import");
  expect(importLeg?.outcome ?? "none").not.toBe("imported");
  expect(log.json.syncHealth.pathMismatch).toBe(0);
});

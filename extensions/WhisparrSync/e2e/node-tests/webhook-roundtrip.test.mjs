// Genuine outside-in webhook round-trip, run by `node --test`. Against a REAL Whisparr v3 (Eros)
// container: register a Webhook notification pointed at Cove, then make Whisparr's OWN event system fire a
// real On-Import (add a movie, drop a file, ManualImport it), and prove Cove hears and ingests it. The
// load-bearing assertion reads the extension's own import log — never the notification POST's status
// — for a webhook-sourced entry that REFERENCES the imported movie, not merely a count bump.
//
// This is the positive round-trip. The hermetic negative path (unsigned/unknown/Test rejection) stays in
// tests/webhook-security.spec.mjs and is not duplicated here.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { startWhisparrSyncHarness, EXTENSION_ID } from "../lib/setup.mjs";
import { registerWebhookNotification, triggerImport } from "../lib/whisparr-webhook.mjs";

// Whisparr posts the webhook from INSIDE its own container, so it must reach Cove by the shared-network
// service alias (`cove:5073`) — never the test process's mapped-host localhost, which the container can't see.
const COVE_WEBHOOK_URL = `http://cove:5073/api/extensions/${EXTENSION_ID}/webhook`;

let ctx;

before(async () => {
  ctx = await startWhisparrSyncHarness({ version: "v3" });
}, { timeout: 600_000 });

after(async () => {
  await ctx?.stop();
}, { timeout: 120_000 });

test("a real Whisparr On-Import round-trips to Cove and is ingested", async () => {
  const { api, whisparr } = ctx;

  // The real minted secret, read from the extension's OWN webhook-url route (never hardcoded): the URL
  // embeds it as ?token=, so lift it back out to carry on the v3 custom X-Cove-Token header.
  const urlRes = await api.get(`/api/extensions/${EXTENSION_ID}/webhook-url`);
  assert.equal(urlRes.status, 200, `webhook-url is readable (body: ${urlRes.text})`);
  const token = new URL(urlRes.json.url).searchParams.get("token");
  assert.ok(token && token.length > 0, "a webhook secret was minted and returned");

  await registerWebhookNotification({ whisparr, version: "v3", coveWebhookUrl: COVE_WEBHOOK_URL, token });

  const imported = await triggerImport({ whisparr, version: "v3" });

  // /import-log no longer carries a per-attempt journal — it was reduced to two bounded aggregates when
  // the store was capped, because nothing rendered the per-entry list and a per-import row grows with the
  // library. So the round-trip is proven by the aggregates instead: the import dependency reaches the
  // "imported" outcome, and the unresolved path-mismatch count stays at zero. That is a STRONGER claim
  // than the old entry lookup, which could match a path without the ingest having succeeded.
  const log = await pollUntil(
    async () => (await api.get(`/api/extensions/${EXTENSION_ID}/import-log`)).json,
    (l) => l?.pipelineHealth?.some((d) => d.dependency === "import" && d.outcome === "imported"),
    { timeoutMs: 120_000, intervalMs: 1000, label: "the import dependency reaching the imported outcome" },
  );

  const importHealth = log.pipelineHealth.find((d) => d.dependency === "import");
  assert.equal(importHealth.outcome, "imported", "Cove ingested the file Whisparr reported importing");
  assert.equal(importHealth.consecutiveFailures, 0, "the import leg records no failure");
  assert.equal(log.syncHealth.pathMismatch, 0, "no unresolved path mismatch — Whisparr and Cove agree on the path");
  assert.ok(log.lastEventTicks > 0, "the inbound webhook event was recorded");

  // The acquisition leg must be healthy too, or an "imported" outcome could be left over from a prior event.
  const acquisition = log.pipelineHealth.find((d) => d.dependency === "acquisition");
  assert.equal(acquisition.outcome, "ok", "the acquisition leg is healthy");
  assert.ok(imported.moviePath, "Whisparr reported a movie path for the import");
});

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

  // Ties the import-log entry to THIS import: the imported file lands under the movie's own folder, so the
  // entry's path is contained by it. A count bump alone would not prove the real event round-tripped.
  const withinMovieFolder = (path) => {
    const norm = (p) => (typeof p === "string" ? p.replace(/\\/g, "/").toLowerCase() : "");
    return norm(path).startsWith(norm(imported.moviePath));
  };
  const matchesImport = (e) =>
    e.source === "webhook" &&
    String(e.eventType).toLowerCase() === "download" &&
    withinMovieFolder(e.path);

  // Read-after-write is not immediate: Whisparr fires the notification asynchronously after the import
  // completes, so poll the import log rather than sleeping a fixed duration.
  const log = await pollUntil(
    async () => (await api.get(`/api/extensions/${EXTENSION_ID}/import-log`)).json,
    (l) => Array.isArray(l?.entries) && l.entries.some(matchesImport),
    { timeoutMs: 120_000, intervalMs: 1000, label: "a webhook On-Import entry referencing the imported movie" },
  );

  const entry = log.entries.find(matchesImport);
  assert.ok(entry, "an import-log entry for the real On-Import round-trip exists");
  assert.equal(entry.source, "webhook", "the entry came in over the inbound webhook");
  assert.equal(String(entry.eventType).toLowerCase(), "download", "it is a Whisparr On-Import (Download) event");
  assert.ok(withinMovieFolder(entry.path), `the entry references the imported movie (path: ${entry.path})`);
});

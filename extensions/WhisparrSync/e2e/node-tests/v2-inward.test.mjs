// Genuine outside-in v2 (Sonarr-shaped) inward round-trip, run by `node --test`. Against a
// REAL Whisparr v2 container: register a Webhook notification, then make Whisparr's OWN event system fire a
// real On-Import (add the site, drop a file, ManualImport it into an episode), and prove Cove hears and
// ingests it. The v2 On-Import payload is series + episode(s) + episodeFile — structurally DISTINCT from v3's
// movie + movieFile — so this exercises Cove's WebhookReceiver v2 parse branch end to end with real
// Whisparr-fired state (not the retired hermetic synthetic reconciliation payload).
//
// v2's Webhook notification has no custom-header field (SkyHook README: Basic-Auth only), so the real
// X-Cove-Token cannot ride the notification directly. It is injected IN TRANSIT by startTokenShim — a reverse
// proxy on the shared network that adds the header and forwards the request UNCHANGED. The fired event and its
// payload stay 100% real and Cove's fail-closed token gate is honoured, never disabled. The hermetic negative
// path (unsigned/unknown/Test rejection) stays in tests/webhook-security.spec.mjs and is not duplicated here.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { startWhisparrSyncHarness, EXTENSION_ID } from "../lib/setup.mjs";
import { registerWebhookNotification, triggerImport, startTokenShim } from "../lib/whisparr-webhook.mjs";

// The v2 Webhook posts to the token shim (a service alias on the shared network), which injects X-Cove-Token
// and forwards to Cove's own alias (cove:5073) — never the test process's mapped-host localhost.
const WEBHOOK_PATH = `/api/extensions/${EXTENSION_ID}/webhook`;

// The v2 On-Import is a Whisparr "Download" event; the reconcile backstop reads the same import from history
// under this eventType (the exact key ReconcileJob collects on).
const IMPORT_HISTORY_EVENT = "downloadFolderImported";

let ctx;
let shim;
// The real import fired in the first test; the reconcile-contract test asserts against the same landed item.
let imported;

before(async () => {
  ctx = await startWhisparrSyncHarness({ version: "v2" });
}, { timeout: 600_000 });

after(async () => {
  await shim?.stop().catch(() => {});
  await ctx?.stop();
}, { timeout: 120_000 });

// Reads Whisparr v2's own Sonarr-shaped API on its mapped host port with the out-of-band key (never the
// extension's), mirroring v2-parity.test.mjs. Used for the history-reconcile contract read.
async function whisparrGet(path) {
  const res = await fetch(`${ctx.whisparr.baseUrlFromHost}${path}`, {
    headers: { "X-Api-Key": ctx.whisparr.apiKey },
  });
  const text = await res.text();
  return { status: res.status, json: text ? JSON.parse(text) : undefined };
}

const norm = (p) => (typeof p === "string" ? p.replace(/\\/g, "/").toLowerCase() : "");
// The imported episode file lands under the site's own folder, so a matching path is contained by it — a
// count bump alone would not prove the real event round-tripped.
const withinSiteFolder = (path) => norm(path).startsWith(norm(imported.seriesPath));

test("a real Whisparr v2 On-Import (series+episode) round-trips to Cove and is ingested", async () => {
  const { api, whisparr } = ctx;

  // The real minted secret, read from the extension's OWN webhook-url route (never hardcoded): the URL embeds
  // it as a token query param, so lift it back out to hand to the token shim.
  const urlRes = await api.get(`/api/extensions/${EXTENSION_ID}/webhook-url`);
  assert.equal(urlRes.status, 200, `webhook-url is readable (body: ${urlRes.text})`);
  const token = new URL(urlRes.json.url).searchParams.get("token");
  assert.ok(token && token.length > 0, "a webhook secret was minted and returned");

  // The shim carries the real token in transit for v2's header-less Webhook — the gate stays fail-closed.
  const networkName = ctx.harness.container.getNetworkNames()[0];
  shim = await startTokenShim({ networkName, token });
  const coveWebhookUrl = `${shim.urlFromWhisparr}${WEBHOOK_PATH}`;

  await registerWebhookNotification({ whisparr, version: "v2", coveWebhookUrl, token });

  imported = await triggerImport({ whisparr, version: "v2" });

  const matchesImport = (e) =>
    e.source === "webhook" &&
    String(e.eventType).toLowerCase() === "download" &&
    withinSiteFolder(e.path);

  // Read-after-write is not immediate: Whisparr fires the notification asynchronously after the import
  // completes, so poll the import log rather than sleeping a fixed duration.
  const log = await pollUntil(
    async () => (await api.get(`/api/extensions/${EXTENSION_ID}/import-log`)).json,
    (l) => Array.isArray(l?.entries) && l.entries.some(matchesImport),
    { timeoutMs: 120_000, intervalMs: 1000, label: "a v2 webhook On-Import entry referencing the imported episode" },
  );

  const entry = log.entries.find(matchesImport);
  assert.ok(entry, "an import-log entry for the real v2 On-Import round-trip exists");
  assert.equal(entry.source, "webhook", "the entry came in over the inbound webhook");
  assert.equal(String(entry.eventType).toLowerCase(), "download", "it is a Whisparr On-Import (Download) event");
  // The v2 payload is series+episode-shaped (episodeFile.Path), distinct from v3's movie payload; a path under
  // the site folder confirms Cove parsed the v2 branch, not merely that the log grew.
  assert.ok(withinSiteFolder(entry.path), `the entry references the imported episode file (path: ${entry.path})`);
});

test("the v2 downloadFolderImported history event reconciles against real Cove state", async () => {
  assert.ok(imported, "the real v2 import from the round-trip test must have run first");
  const { api } = ctx;

  // The v2 inward RECONCILE contract, read from Whisparr's OWN history (never a synthesized payload): the real
  // import surfaces as a downloadFolderImported record whose data map carries the imported path — importedPath
  // (the final in-library path) PREFERRED, falling back to droppedPath — the exact resolution
  // ReconcileJob.ResolveImportedPath performs to drive an ingest. Poll: the history row lands asynchronously
  // after the ManualImport completes.
  const valueOf = (data, name) => {
    const key = Object.keys(data).find((k) => k.toLowerCase() === name.toLowerCase() && data[k]);
    return key ? data[key] : undefined;
  };
  const pathOf = (record) => {
    const data = record?.data ?? {};
    return valueOf(data, "importedPath") ?? valueOf(data, "droppedPath");
  };
  const isRealImport = (record) =>
    String(record?.eventType).toLowerCase() === IMPORT_HISTORY_EVENT.toLowerCase() && withinSiteFolder(pathOf(record));

  const records = await pollUntil(
    async () => {
      const { json } = await whisparrGet("/api/v3/history?page=1&pageSize=50&sortKey=date&sortDirection=descending");
      return Array.isArray(json?.records) ? json.records : [];
    },
    (recs) => recs.some(isRealImport),
    { timeoutMs: 120_000, intervalMs: 1000, label: "a v2 downloadFolderImported history row for the imported episode" },
  );

  const record = records.find(isRealImport);
  assert.ok(record, "Whisparr v2 recorded a real downloadFolderImported history event");
  assert.equal(
    String(record.eventType).toLowerCase(),
    IMPORT_HISTORY_EVENT.toLowerCase(),
    "the reconcile contract eventType matches the real v2 wire value",
  );
  const importedPath = pathOf(record);
  assert.ok(withinSiteFolder(importedPath), `the history row carries the real imported path (${importedPath})`);

  // Tie the genuine v2 history event to REAL Cove state: the same imported path Cove already ingested (via the
  // round-trip's webhook entry) is the path this downloadFolderImported record exposes — so the reconcile
  // backstop, which derives the identical shared ImportKey from this history row, drives the same Cove item.
  const log = (await api.get(`/api/extensions/${EXTENSION_ID}/import-log`)).json;
  const coveEntry = log.entries.find((e) => withinSiteFolder(e.path));
  assert.ok(coveEntry, "Cove's real ingest state reflects the imported episode the v2 history event references");
  assert.equal(
    norm(coveEntry.path),
    norm(importedPath),
    "the ingested Cove path and the v2 history importedPath are the same real file (one import, two channels)",
  );
});

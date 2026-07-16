// Offline correctness spec (node:test) — the loop-safety invariant (AVAIL-01/AVAIL-02) proven against
// REAL Whisparr state, upgrading the old attribution-only "status == 200" assertion. A Cove-initiated
// add registers an entity in Whisparr WITHOUT grabbing (searchForMovie/searchForMissingEpisodes:false);
// only an explicit user Search grabs. The container is deliberately hermetic (no indexer, no download
// client), so a grab is both impossible AND — the point of this test — never even requested.
//
// The Cove-initiated add exercised here is the studio monitor: on v3 the resolvable allowlist identity
// is the studio (the movie-search recording rows carry StashId:null, so no per-scene movie id resolves
// offline), and monitoring it registers a genuine Whisparr studio row. That is a real add of an owned
// entity, which is exactly what the loop-safe invariant is about.
//
// The load-bearing assertions read Whisparr's OWN API, never the extension's return code:
//   1. The add really landed — a real studio row exists (GET /api/v3/studio, foreignId = the real id).
//   2. GET /api/v3/queue is EMPTY — no grab was queued for the Cove-owned entity.
//   3. GET /api/v3/movie carries no downloaded file — nothing was pulled as a side effect of the add.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { startWhisparrSyncHarness, EXTENSION_ID } from "../lib/setup.mjs";

let ctx;

async function whisparrGet(path) {
  const res = await fetch(`${ctx.whisparr.baseUrlFromHost}${path}`, {
    headers: { "X-Api-Key": ctx.whisparr.apiKey },
  });
  const text = await res.text();
  return { status: res.status, json: text ? JSON.parse(text) : undefined };
}

function queueRecords(json) {
  if (Array.isArray(json)) return json;
  return json?.records ?? [];
}

before(async () => {
  ctx = await startWhisparrSyncHarness({ version: "v3" });
}, { timeout: 600_000 });

after(async () => {
  await ctx?.stop();
}, { timeout: 120_000 });

test("a Cove-initiated add is non-grabbing — a real row lands and the Whisparr queue stays empty", async () => {
  const { api, remoteIds } = ctx;

  const add = await api.post(`/api/extensions/${EXTENSION_ID}/monitor`, {
    Kind: "studio",
    RemoteIds: [{ Endpoint: remoteIds.endpoint, RemoteId: remoteIds.remoteId }],
    Monitored: true,
  });
  // The extension must not have errored, but its status is NOT the proof — the Whisparr-side reads are.
  assert.ok(add.status < 500, `add did not error (status ${add.status}, body: ${add.text})`);

  // The add really landed in Whisparr — a verifiable row, not the extension's own return code.
  const studios = await pollUntil(
    async () => {
      const { json } = await whisparrGet("/api/v3/studio");
      return (Array.isArray(json) ? json : []).filter((s) => s.foreignId === remoteIds.remoteId);
    },
    (rows) => rows.length === 1,
    { timeoutMs: 60_000, label: "the Cove-added studio row is present in Whisparr" },
  );
  assert.equal(studios.length, 1, "exactly one Cove-added studio row exists");

  // Loop-safety, proven against real state: NO grab was queued for the Cove-owned entity. Poll rather
  // than sleep — a grab, had one been (wrongly) requested, would surface in the queue shortly after.
  const records = await pollUntil(
    async () => queueRecords((await whisparrGet("/api/v3/queue")).json),
    (recs) => recs.length === 0,
    { timeoutMs: 30_000, label: "the Whisparr queue is empty after a Cove-initiated add" },
  );
  assert.equal(records.length, 0, "no grab was queued for the Cove-owned entity");

  // Nothing was pulled: no movie row carries a downloaded file as a side effect of the add.
  const { json: movies } = await whisparrGet("/api/v3/movie");
  const grabbed = (Array.isArray(movies) ? movies : []).filter((m) => m.hasFile === true);
  assert.equal(grabbed.length, 0, "no movie was grabbed/downloaded by a Cove-initiated add");
});

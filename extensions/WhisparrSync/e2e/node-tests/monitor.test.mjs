// Offline correctness spec (node:test) — the real-state monitor proof. Monitoring a studio by a REAL allowlist StashDB
// id (resolved offline through the SkyHook replay stub) must produce genuine, assertable Whisparr-side
// state, not the attribution-only HANDLED no-op the synthetic-fixture suite could only prove did not
// crash. The load-bearing assertions read Whisparr's OWN API — never the extension's return code:
//
//   1. GET /api/v3/studio finds the row whose foreignId is the real allowlist id, monitored:true
//      (a v3 studio monitor creates a Whisparr STUDIO row via POST /api/v3/studio — the movie library
//      is a separate entity, so the real-state read targets /studio here, not /movie).
//   2. The cove-sync origin tag is attached to that row (tag id resolved via GET /api/v3/tag).
//   3. GET /api/v3/movie proves the loop-safe invariant: merely monitoring a studio grabs nothing —
//      no movie row is downloaded as a side effect (searchForMovie:false / no explicit Search).
//   4. A repeated identical monitor call is idempotent (409/exists = success): still exactly one
//      monitored, tagged studio row, never a duplicate.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { startWhisparrSyncHarness, EXTENSION_ID } from "../lib/setup.mjs";

const ORIGIN_TAG = "cove-sync";

let ctx;

// Reads Whisparr's own API on its mapped host port with the out-of-band key (never the extension's).
async function whisparrGet(path) {
  const res = await fetch(`${ctx.whisparr.baseUrlFromHost}${path}`, {
    headers: { "X-Api-Key": ctx.whisparr.apiKey },
  });
  const text = await res.text();
  return { status: res.status, json: text ? JSON.parse(text) : undefined };
}

async function findStudio(foreignId) {
  const { json } = await whisparrGet("/api/v3/studio");
  const rows = Array.isArray(json) ? json : [];
  return rows.filter((s) => s.foreignId === foreignId);
}

before(async () => {
  ctx = await startWhisparrSyncHarness({ version: "v3" });
}, { timeout: 600_000 });

after(async () => {
  await ctx?.stop();
}, { timeout: 120_000 });

test("monitoring a studio adds a real, monitored, cove-sync-tagged Whisparr studio row", async () => {
  const { api, remoteIds } = ctx;
  const body = {
    Kind: "studio",
    RemoteIds: [{ Endpoint: remoteIds.endpoint, RemoteId: remoteIds.remoteId }],
    Monitored: true,
  };

  const res = await api.post(`/api/extensions/${EXTENSION_ID}/monitor`, body);
  // The extension must not have errored, but its status is NOT the proof of sync — the Whisparr-side
  // read below is. Never assert success from the extension's own status code alone.
  assert.ok(res.status < 500, `monitor did not error (status ${res.status}, body: ${res.text})`);

  // Read-after-write is not guaranteed on the first GET: a fresh studio CREATE queues a RefreshStudios
  // that rebuilds the row after the flip PUT, so poll until the real monitored row is observable.
  const matches = await pollUntil(
    () => findStudio(remoteIds.remoteId),
    (rows) => rows.length === 1 && rows[0].monitored === true,
    { timeoutMs: 60_000, label: "a single monitored studio row for the allowlist id" },
  );
  const studio = matches[0];
  assert.equal(studio.foreignId, remoteIds.remoteId);
  assert.equal(studio.monitored, true);

  // The cove-sync origin tag is attached — resolve its id from Whisparr's own tag list, then confirm
  // membership on the studio row (the loop-safety attribution contract, proven against real state).
  const { json: tags } = await whisparrGet("/api/v3/tag");
  const originTag = (Array.isArray(tags) ? tags : []).find((t) => t.label === ORIGIN_TAG);
  assert.ok(originTag, `the ${ORIGIN_TAG} tag exists in Whisparr`);
  assert.ok(
    Array.isArray(studio.tags) && studio.tags.includes(originTag.id),
    `the studio row carries the ${ORIGIN_TAG} tag`,
  );

  // Loop-safety: turning monitoring on grabs nothing. No movie row is downloaded as a side effect.
  const { json: movies } = await whisparrGet("/api/v3/movie");
  const grabbed = (Array.isArray(movies) ? movies : []).filter((m) => m.hasFile === true);
  assert.equal(grabbed.length, 0, "monitoring a studio downloaded no movie files");
});

test("a repeated identical monitor call is idempotent — one row, still monitored and tagged", async () => {
  const { api, remoteIds } = ctx;
  const body = {
    Kind: "studio",
    RemoteIds: [{ Endpoint: remoteIds.endpoint, RemoteId: remoteIds.remoteId }],
    Monitored: true,
  };

  const res = await api.post(`/api/extensions/${EXTENSION_ID}/monitor`, body);
  assert.ok(res.status < 500, `re-toggle did not error (status ${res.status}, body: ${res.text})`);

  const matches = await pollUntil(
    () => findStudio(remoteIds.remoteId),
    (rows) => rows.length === 1 && rows[0].monitored === true,
    { timeoutMs: 60_000, label: "still exactly one monitored studio row after re-toggle" },
  );
  assert.equal(matches.length, 1, "no duplicate studio row was created");

  const { json: tags } = await whisparrGet("/api/v3/tag");
  const originTag = (Array.isArray(tags) ? tags : []).find((t) => t.label === ORIGIN_TAG);
  assert.ok(originTag && matches[0].tags?.includes(originTag.id), `still carries the ${ORIGIN_TAG} tag`);
});

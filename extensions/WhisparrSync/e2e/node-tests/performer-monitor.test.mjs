// Offline correctness spec (node:test) — the real-state PERFORMER monitor proof (v3-only; v2 has no
// performer entity). Monitoring a performer by a REAL allowlist StashDB id (resolved offline through the
// SkyHook replay stub's /performer/{id} recording) must produce genuine, assertable Whisparr-side state.
// Mirrors monitor.test.mjs (studio) for the performer path (SetPerformerMonitorAsync). Load-bearing
// assertions read Whisparr's OWN API — never the extension's return code:
//
//   1. GET /api/v3/performer finds the row whose foreignId is the real allowlist id, monitored:true.
//   2. The cove-sync origin tag is attached (tag id resolved via GET /api/v3/tag).
//   3. GET /api/v3/movie proves loop-safety: monitoring a performer grabs nothing (no downloaded file).
//   4. A repeated identical monitor call is idempotent — still exactly one monitored, tagged row.
import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { pollUntil } from "@cove-extensions/e2e/poll";
import { startWhisparrSyncHarness, EXTENSION_ID } from "../lib/setup.mjs";

const ORIGIN_TAG = "cove-sync";

let ctx;

async function whisparrGet(path) {
  const res = await fetch(`${ctx.whisparr.baseUrlFromHost}${path}`, {
    headers: { "X-Api-Key": ctx.whisparr.apiKey },
  });
  const text = await res.text();
  return { status: res.status, json: text ? JSON.parse(text) : undefined };
}

async function findPerformer(foreignId) {
  const { json } = await whisparrGet("/api/v3/performer");
  const rows = Array.isArray(json) ? json : [];
  return rows.filter((p) => p.foreignId === foreignId);
}

before(async () => {
  ctx = await startWhisparrSyncHarness({ version: "v3" });
}, { timeout: 600_000 });

after(async () => {
  await ctx?.stop();
}, { timeout: 120_000 });

test("monitoring a performer adds a real, monitored, cove-sync-tagged Whisparr performer row", async () => {
  const { api, remoteIds } = ctx;
  assert.ok(remoteIds.performerRemoteId, "the allowlist yielded a performer StashDB id");
  const body = {
    Kind: "performer",
    RemoteIds: [{ Endpoint: remoteIds.endpoint, RemoteId: remoteIds.performerRemoteId }],
    Monitored: true,
  };

  const res = await api.post(`/api/extensions/${EXTENSION_ID}/monitor`, body);
  assert.ok(res.status < 500, `performer monitor did not error (status ${res.status}, body: ${res.text})`);

  // A fresh performer CREATE queues a RefreshPerformers that rebuilds the row after the flip PUT, so poll.
  const matches = await pollUntil(
    () => findPerformer(remoteIds.performerRemoteId),
    (rows) => rows.length === 1 && rows[0].monitored === true,
    { timeoutMs: 60_000, label: "a single monitored performer row for the allowlist id" },
  );
  const performer = matches[0];
  assert.equal(performer.foreignId, remoteIds.performerRemoteId);
  assert.equal(performer.monitored, true);

  // The cove-sync origin tag is attached (loop-safety attribution), proven against real state.
  const { json: tags } = await whisparrGet("/api/v3/tag");
  const originTag = (Array.isArray(tags) ? tags : []).find((t) => t.label === ORIGIN_TAG);
  assert.ok(originTag, `the ${ORIGIN_TAG} tag exists in Whisparr`);
  assert.ok(
    Array.isArray(performer.tags) && performer.tags.includes(originTag.id),
    `the performer row carries the ${ORIGIN_TAG} tag`,
  );

  // Loop-safety: turning monitoring on grabs nothing.
  const { json: movies } = await whisparrGet("/api/v3/movie");
  const grabbed = (Array.isArray(movies) ? movies : []).filter((m) => m.hasFile === true);
  assert.equal(grabbed.length, 0, "monitoring a performer downloaded no movie files");
});

test("a repeated identical performer monitor call is idempotent — one row, still monitored and tagged", async () => {
  const { api, remoteIds } = ctx;
  const body = {
    Kind: "performer",
    RemoteIds: [{ Endpoint: remoteIds.endpoint, RemoteId: remoteIds.performerRemoteId }],
    Monitored: true,
  };

  const res = await api.post(`/api/extensions/${EXTENSION_ID}/monitor`, body);
  assert.ok(res.status < 500, `re-toggle did not error (status ${res.status}, body: ${res.text})`);

  const matches = await pollUntil(
    () => findPerformer(remoteIds.performerRemoteId),
    (rows) => rows.length === 1 && rows[0].monitored === true,
    { timeoutMs: 60_000, label: "still exactly one monitored performer row after re-toggle" },
  );
  assert.equal(matches.length, 1, "no duplicate performer row was created");

  const { json: tags } = await whisparrGet("/api/v3/tag");
  const originTag = (Array.isArray(tags) ? tags : []).find((t) => t.label === ORIGIN_TAG);
  assert.ok(originTag && matches[0].tags?.includes(originTag.id), `still carries the ${ORIGIN_TAG} tag`);
});

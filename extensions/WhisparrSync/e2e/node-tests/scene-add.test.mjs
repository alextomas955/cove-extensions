// Offline correctness spec (node:test) — the owned-scene push path proven against REAL Whisparr state.
// This is the per-scene analogue of the studio monitor: a Cove video carrying a REAL allowlist StashDB
// SCENE id (resolved offline through the SkyHook replay stub) is pushed via /scene-add, which registers
// it in Whisparr as a monitored-but-non-grabbing MOVIE. It closes the gap the studio-only proof left —
// the scene-search recording rows carry StashId:null, so the reserved video's per-scene StashDB id is
// the one identity that resolves a genuine per-scene movie add offline.
//
// The load-bearing assertions read Whisparr's OWN API, never the extension's return code:
//   1. The add really landed — a real movie row exists (GET /api/v3/movie, foreignId = the scene's
//      StashDB id), which is the actual per-scene push, not the extension's status.
//   2. That movie carries the cove-sync origin tag (tag id resolved via GET /api/v3/tag).
//   3. GET /api/v3/queue is EMPTY and the movie has no downloaded file — the add is non-grabbing
//      (searchForMovie:false); only an explicit Search grabs.
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

test("pushing a Cove-owned scene adds a real, cove-sync-tagged, non-grabbing Whisparr movie", async () => {
  const { api, remoteIds } = ctx;

  // The harness reserves one seeded video to carry the per-scene StashDB id — /scene-add resolves the
  // scene server-side from this Cove id, so without it there is nothing to push.
  assert.ok(remoteIds.sceneVideoId, "a seeded video was reserved to carry the per-scene StashDB id");
  assert.ok(remoteIds.sceneRemoteId, "the allowlist yielded a per-scene StashDB id");

  const add = await api.post(`/api/extensions/${EXTENSION_ID}/scene-add`, {
    CoveId: remoteIds.sceneVideoId,
  });
  // The extension must not have errored, but its status is NOT the proof — the Whisparr-side reads are.
  assert.ok(add.status < 500, `scene-add did not error (status ${add.status}, body: ${add.text})`);

  // The add really landed in Whisparr — a real movie row keyed by the scene's StashDB id.
  const movies = await pollUntil(
    async () => {
      const { json } = await whisparrGet("/api/v3/movie");
      return (Array.isArray(json) ? json : []).filter((m) => m.foreignId === remoteIds.sceneRemoteId);
    },
    (rows) => rows.length === 1,
    { timeoutMs: 60_000, label: "the Cove-pushed scene is present as a Whisparr movie row" },
  );
  assert.equal(movies.length, 1, "exactly one Cove-pushed movie row exists for the scene");
  const movie = movies[0];
  assert.equal(movie.foreignId, remoteIds.sceneRemoteId, "the movie's foreignId is the scene's StashDB id");

  // The cove-sync origin tag is attached — resolve its id from Whisparr's own tag list, then confirm
  // membership on the movie row (the loop-safety attribution contract, proven against real state).
  const { json: tags } = await whisparrGet("/api/v3/tag");
  const originTag = (Array.isArray(tags) ? tags : []).find((t) => t.label === ORIGIN_TAG);
  assert.ok(originTag, `the ${ORIGIN_TAG} tag exists in Whisparr`);
  assert.ok(
    Array.isArray(movie.tags) && movie.tags.includes(originTag.id),
    `the pushed movie row carries the ${ORIGIN_TAG} tag`,
  );

  // Loop-safety, proven against real state: NO grab was queued for the Cove-owned scene, and the movie
  // itself carries no downloaded file. Poll rather than sleep — a wrongly-requested grab surfaces shortly.
  const records = await pollUntil(
    async () => queueRecords((await whisparrGet("/api/v3/queue")).json),
    (recs) => recs.length === 0,
    { timeoutMs: 30_000, label: "the Whisparr queue is empty after a Cove-initiated scene add" },
  );
  assert.equal(records.length, 0, "no grab was queued for the Cove-owned scene");
  assert.notEqual(movie.hasFile, true, "the pushed movie was not grabbed/downloaded");
});

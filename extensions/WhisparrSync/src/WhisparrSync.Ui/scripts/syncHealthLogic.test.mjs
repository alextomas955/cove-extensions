/**
 * Behavior contract for the pure sync-health logic. The runner (check-sync-health-logic.mjs) compiles
 * syncHealthLogic.ts and passes the compiled module URL via SYNC_HEALTH_LOGIC_MODULE.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.SYNC_HEALTH_LOGIC_MODULE);
const { syncHealthFromServer, hasSyncProblem, syncProblemSummary, NO_SYNC_PROBLEMS } = mod;

test("syncHealthFromServer: reads a valid payload; malformed input is healthy", () => {
  const h = syncHealthFromServer({
    pathMismatch: 2,
    lastMismatchTicks: 123,
    samplePaths: ["/data/media/a.mp4", 7, "", "/data/media/b.mp4"],
  });
  assert.equal(h.pathMismatch, 2);
  assert.equal(h.lastMismatchTicks, 123);
  assert.deepEqual(h.samplePaths, ["/data/media/a.mp4", "/data/media/b.mp4"]); // non-strings/empties dropped

  assert.deepEqual(syncHealthFromServer(null), NO_SYNC_PROBLEMS);
  assert.deepEqual(syncHealthFromServer("nope"), NO_SYNC_PROBLEMS);
  assert.deepEqual(syncHealthFromServer({}), NO_SYNC_PROBLEMS);
  assert.equal(syncHealthFromServer({ pathMismatch: -3 }).pathMismatch, 0); // negatives clamp to 0
});

test("hasSyncProblem: true only when there is at least one unresolved mismatch", () => {
  assert.equal(hasSyncProblem({ pathMismatch: 1, lastMismatchTicks: 1, samplePaths: [] }), true);
  assert.equal(hasSyncProblem(NO_SYNC_PROBLEMS), false);
});

test("syncProblemSummary: null when healthy; singular/plural + same-path cause when broken", () => {
  assert.equal(syncProblemSummary(NO_SYNC_PROBLEMS), null);

  const one = syncProblemSummary({ pathMismatch: 1, lastMismatchTicks: 1, samplePaths: [] });
  assert.match(one, /1 recent import /); // singular, no trailing 's'
  assert.match(one, /same path/i); // names the root cause

  const many = syncProblemSummary({ pathMismatch: 4, lastMismatchTicks: 1, samplePaths: [] });
  assert.match(many, /4 recent imports/); // plural
});

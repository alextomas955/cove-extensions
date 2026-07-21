/**
 * Behavior contract for the pure batch-coalescing engine. The runner (check-batch-coalescer-logic.mjs)
 * compiles batchCoalescerLogic.ts and passes the compiled module URL via BATCH_COALESCER_LOGIC_MODULE.
 *
 * A manual scheduler captures the flush callback so each test drives the tick deterministically; `settle`
 * runs the captured flush and drains the microtask queue that the async fetch resolves on.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.BATCH_COALESCER_LOGIC_MODULE);
const { createBatchCoalescer } = mod;

function harness(fetchBatch) {
  let pending = null;
  const store = createBatchCoalescer(fetchBatch, (flush) => {
    pending = flush;
  });
  async function settle() {
    const flush = pending;
    pending = null;
    if (flush) flush();
    await new Promise((resolve) => setTimeout(resolve, 0)); // drain the async fetch's microtasks
  }
  return { store, settle, scheduled: () => pending !== null };
}

test("coalesces every key in one tick into a single fetchBatch call", async () => {
  const calls = [];
  const { store, settle } = harness(async (keys) => {
    calls.push([...keys]);
    return new Map(keys.map((k) => [k, { v: k }]));
  });

  store.request("1");
  store.request("2");
  store.request("1"); // duplicate in the same tick collapses
  await settle();

  assert.equal(calls.length, 1);
  assert.deepEqual(calls[0].sort(), ["1", "2"]);
  assert.deepEqual(store.get("1"), { v: "1" });
  assert.deepEqual(store.get("2"), { v: "2" });
});

test("a cached key never re-fetches", async () => {
  let fetches = 0;
  const { store, settle } = harness(async (keys) => {
    fetches += 1;
    return new Map(keys.map((k) => [k, { v: k }]));
  });

  store.request("1");
  await settle();
  store.request("1"); // already cached → no new tick scheduled
  await settle();

  assert.equal(fetches, 1);
});

test("a key missing from the returned map caches as null (fetched, no status)", async () => {
  const { store, settle } = harness(async () => new Map()); // returns nothing for the requested key

  store.request("7");
  await settle();

  assert.equal(store.get("7"), null);
  // Cached-as-null must NOT re-fetch: request again, and with nothing scheduled the value stays null.
  store.request("7");
  assert.equal(store.get("7"), null);
});

test("a throwing fetchBatch resolves every requested key to null, never rethrows", async () => {
  let fetches = 0;
  const { store, settle } = harness(async () => {
    fetches += 1;
    throw new Error("unreachable");
  });

  store.request("a");
  store.request("b");
  await settle();

  assert.equal(store.get("a"), null);
  assert.equal(store.get("b"), null);
  // The keys are cached (as null) so they do not hammer a broken backend on every render.
  store.request("a");
  await settle();
  assert.equal(fetches, 1);
});

test("subscribers are notified once per flush and unsubscribe cleanly", async () => {
  const { store, settle } = harness(async (keys) => new Map(keys.map((k) => [k, k])));

  let hits = 0;
  const unsubscribe = store.subscribe(() => {
    hits += 1;
  });

  store.request("x");
  await settle();
  assert.equal(hits, 1);

  unsubscribe();
  store.request("y");
  await settle();
  assert.equal(hits, 1); // no further notification after unsubscribe
});

test("evict drops only the named keys; an evicted key re-fetches while an untouched key stays cached", async () => {
  const calls = [];
  const { store, settle } = harness(async (keys) => {
    calls.push([...keys].sort());
    return new Map(keys.map((k) => [k, { v: k }]));
  });

  store.request("a");
  store.request("b");
  await settle();
  assert.deepEqual(calls, [["a", "b"]]);

  store.evict(["a"]);
  store.request("a"); // evicted → re-fetches
  store.request("b"); // still cached → short-circuits, not re-fetched
  await settle();

  assert.deepEqual(calls, [["a", "b"], ["a"]]);
  assert.deepEqual(store.get("a"), { v: "a" });
  assert.deepEqual(store.get("b"), { v: "b" });
});

test("evict on a not-yet-cached key is a harmless no-op", async () => {
  const { store, settle } = harness(async (keys) => new Map(keys.map((k) => [k, { v: k }])));

  assert.doesNotThrow(() => {
    store.evict(["never-cached"]);
  });
  store.request("never-cached");
  await settle();
  assert.deepEqual(store.get("never-cached"), { v: "never-cached" });
});

test("evict does not notify subscribers on its own", async () => {
  const { store, settle } = harness(async (keys) => new Map(keys.map((k) => [k, { v: k }])));

  store.request("a");
  await settle();

  let hits = 0;
  store.subscribe(() => {
    hits += 1;
  });
  store.evict(["a"]); // eviction alone must not notify — the follow-up re-request's flush notifies
  assert.equal(hits, 0);
});

test("evict then request within one tick coalesces into ONE fetchBatch call with exactly those keys", async () => {
  const calls = [];
  const { store, settle } = harness(async (keys) => {
    calls.push([...keys].sort());
    return new Map(keys.map((k) => [k, { v: k }]));
  });

  store.request("a");
  store.request("b");
  await settle();
  calls.length = 0;

  store.evict(["a", "b"]);
  store.request("a");
  store.request("b");
  await settle();

  assert.equal(calls.length, 1);
  assert.deepEqual(calls[0], ["a", "b"]);
});

test("clear drops the cache so a key re-fetches", async () => {
  let fetches = 0;
  const { store, settle } = harness(async (keys) => {
    fetches += 1;
    return new Map(keys.map((k) => [k, k]));
  });

  store.request("1");
  await settle();
  assert.equal(fetches, 1);

  store.clear();
  store.request("1");
  await settle();
  assert.equal(fetches, 2); // cache was cleared, so the same key fetches again
});

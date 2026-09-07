/**
 * Behavior contract for the pure gen-guarded resource-entry lifecycle. The runner
 * (check-resource-entry-logic.mjs) compiles resourceEntryLogic.ts and passes the compiled module URL via
 * RESOURCE_ENTRY_LOGIC_MODULE.
 *
 * `deferred()` hands each load a promise the test resolves by hand, so gen-guard ordering (a stale load
 * settling AFTER a newer one) is driven deterministically.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.RESOURCE_ENTRY_LOGIC_MODULE);
const { newResourceEntry, emit, runLoad, pruneIdleEntries } = mod;

function deferred() {
  let resolve;
  const promise = new Promise((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

const flag = (loading) => (prev) => (prev.loading ? prev : { ...prev, loading });
const settle = () => new Promise((r) => setTimeout(r, 0));

test("emit updates state and notifies every subscriber", () => {
  const entry = newResourceEntry({ value: 0, loading: false });
  let hits = 0;
  entry.listeners.add(() => {
    hits += 1;
  });
  entry.listeners.add(() => {
    hits += 1;
  });

  emit(entry, { value: 9, loading: false });

  assert.equal(entry.state.value, 9);
  assert.equal(hits, 2);
});

test("runLoad flags loading, then emits the fetched state and clears inflight", async () => {
  const entry = newResourceEntry({ value: 0, loading: false });
  const seen = [];
  entry.listeners.add(() => seen.push({ ...entry.state }));

  await runLoad(entry, flag(true), async () => ({ value: 42, loading: false }));

  assert.deepEqual(seen[0], { value: 0, loading: true }); // loading flagged first
  assert.deepEqual(entry.state, { value: 42, loading: false }); // then the resolved state
  assert.equal(entry.inflight, null); // dedupe released after settle
});

test("an already-loading entry does not re-emit the loading flag", async () => {
  const entry = newResourceEntry({ value: 0, loading: true }); // already loading
  let notifications = 0;
  entry.listeners.add(() => {
    notifications += 1;
  });

  await runLoad(entry, flag(true), async () => ({ value: 1, loading: false }));

  assert.equal(notifications, 1); // only the final emit, no redundant loading emit
});

test("gen guard: a superseded load never emits, even if it resolves last", async () => {
  const entry = newResourceEntry({ value: 0, loading: false });
  const first = deferred();
  const second = deferred();

  const p1 = runLoad(entry, flag(true), async () => first.promise);
  const p2 = runLoad(entry, flag(true), async () => second.promise); // supersedes the first

  second.resolve({ value: 2, loading: false });
  await p2;
  assert.deepEqual(entry.state, { value: 2, loading: false });

  first.resolve({ value: 1, loading: false }); // stale winner resolves LAST
  await p1;
  assert.deepEqual(entry.state, { value: 2, loading: false }); // still the newer result, never clobbered
});

test("gen guard: a stale load settling late does not clear a newer inflight", async () => {
  const entry = newResourceEntry({ value: 0, loading: false });
  const first = deferred();

  const p1 = runLoad(entry, flag(true), async () => first.promise);
  // A newer load is in flight (its own deferred never resolves here).
  const p2 = runLoad(entry, flag(true), async () => new Promise(() => undefined));
  const newestInflight = entry.inflight;
  assert.notEqual(newestInflight, null);

  first.resolve({ value: 1, loading: false }); // the stale load settles
  await p1;
  await settle();

  assert.equal(entry.inflight, newestInflight); // the newer inflight handle is intact
  void p2; // p2 never settles; intentional
});

test("pruneIdleEntries drops the oldest idle entries once past the cap", () => {
  const entries = new Map();
  for (let i = 0; i < 12; i++) {
    entries.set(i, newResourceEntry({ value: i, loading: false }));
  }

  const dropped = pruneIdleEntries(entries, 10);

  assert.equal(dropped, 2);
  assert.equal(entries.size, 10);
  assert.equal(entries.has(0), false); // oldest-first
  assert.equal(entries.has(1), false);
  assert.equal(entries.has(11), true);
});

test("pruneIdleEntries is a no-op at or under the cap", () => {
  const entries = new Map();
  for (let i = 0; i < 10; i++) {
    entries.set(i, newResourceEntry({ value: i, loading: false }));
  }

  assert.equal(pruneIdleEntries(entries, 10), 0);
  assert.equal(entries.size, 10);
});

test("pruneIdleEntries never drops a subscribed or in-flight entry", () => {
  const entries = new Map();
  const subscribed = newResourceEntry({ value: 0, loading: false });
  subscribed.listeners.add(() => undefined);
  const loading = newResourceEntry({ value: 1, loading: true });
  loading.inflight = new Promise(() => undefined);

  // Both live entries are the OLDEST, so an order-only eviction would take them first.
  entries.set("subscribed", subscribed);
  entries.set("loading", loading);
  for (let i = 0; i < 3; i++) {
    entries.set(i, newResourceEntry({ value: i, loading: false }));
  }

  const dropped = pruneIdleEntries(entries, 2);

  // Only the three idle entries are eligible; the map stays above the cap rather than orphaning a mounted
  // component's snapshot or dropping a load its awaiter holds.
  assert.equal(dropped, 3);
  assert.equal(entries.size, 2);
  assert.equal(entries.has("subscribed"), true);
  assert.equal(entries.has("loading"), true);
});

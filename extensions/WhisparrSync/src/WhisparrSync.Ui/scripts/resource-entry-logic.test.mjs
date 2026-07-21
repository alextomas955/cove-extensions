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
const { newResourceEntry, emit, runLoad } = mod;

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

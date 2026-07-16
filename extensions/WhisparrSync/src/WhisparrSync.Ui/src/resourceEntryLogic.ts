/**
 * The gen-guarded, in-flight-deduped resource-entry lifecycle shared by the status stores that back a
 * `useSyncExternalStore` hook (monitor status, scene detail, library summary). Each entry holds one resource's
 * resolved state, the set of React subscribers, the in-flight fetch (so concurrent mounts share one call), and
 * a generation counter: a fetch captures the generation it started at and emits its result ONLY if no newer
 * fetch has since bumped it, so a refresh fired mid-flight always wins and a stale response never clobbers a
 * fresh one.
 *
 * Extracted import-free (no React, no SDK) so this lifecycle is offline-testable; each store keeps its own
 * React hook + wire shape and maps BOTH success and handled failure to a next state via the `fetchState` it
 * passes here.
 */

export interface ResourceEntry<S> {
  state: S;
  listeners: Set<() => void>;
  inflight: Promise<void> | null;
  /** Bumped per fetch; a stale in-flight fetch guards its emit so a newer refresh always wins. */
  gen: number;
}

export function newResourceEntry<S>(initial: S): ResourceEntry<S> {
  return { state: initial, listeners: new Set(), inflight: null, gen: 0 };
}

/** Set the entry's state and notify every subscriber. */
export function emit<S>(entry: ResourceEntry<S>, next: S): void {
  entry.state = next;
  for (const listen of entry.listeners) {
    listen();
  }
}

/**
 * Run one gen-guarded load: bump the generation, apply `toLoading` to flag loading (emitting only when it
 * actually changes the state, so an already-loading entry does not re-notify), then await `fetchState` — which
 * resolves to the next state for BOTH success and handled failure, so this core never sees the wire shape or a
 * throw. The result is emitted only if this load is still the newest (gen unchanged), and the in-flight handle
 * is cleared in `finally` (also gen-guarded) so a newer load's handle is never wiped by an older one settling
 * late. Returns the fetch promise, which is also stored on `entry.inflight` for dedupe.
 */
export function runLoad<S>(
  entry: ResourceEntry<S>,
  toLoading: (prev: S) => S,
  fetchState: () => Promise<S>,
): Promise<void> {
  const gen = ++entry.gen;
  const loading = toLoading(entry.state);
  if (loading !== entry.state) {
    emit(entry, loading);
  }

  const promise = (async () => {
    const next = await fetchState();
    if (entry.gen === gen) {
      emit(entry, next);
    }
  })().finally(() => {
    if (entry.gen === gen) {
      entry.inflight = null;
    }
  });

  entry.inflight = promise;
  return promise;
}

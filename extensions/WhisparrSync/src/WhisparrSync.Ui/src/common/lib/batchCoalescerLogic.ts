/**
 * The request-coalescing engine shared by the per-card status stores (scene cards, studio/performer cards).
 * Every key requested within one scheduler tick is folded into ONE batch fetch, the result cached per
 * session, and subscribers notified once — so a 40-card page costs a single Whisparr fetch, not one per card.
 *
 * Extracted import-free (no React, no SDK) so the cache / coalesce / notify state machine is offline-testable;
 * the React `use*` hook that subscribes to a coalescer lives in each store file.
 *
 * Keys are strings so a composite key (e.g. `"studio:42"`) coalesces exactly like a bare id — the caller owns
 * the key format. The injected `fetchBatch` owns the wire shape AND any per-group fan-out: it receives every
 * un-cached key needed this tick and returns a value (or `null` for "fetched, no status") for each. A throw
 * from `fetchBatch` resolves every requested key to `null`, so a status read never crashes a card.
 */

/**
 * The retained-entry ceiling. A library here reaches millions of scenes, and a card's status is requested as it
 * scrolls into view, so an uncapped cache grows with how far the reader scrolled rather than with what is on
 * screen. Well above any page's card count, so the entries a visible page needs are never the ones evicted.
 */
export const MAX_CACHED_ENTRIES = 2000;

export interface BatchCoalescer<V> {
  /** Queue a key for the next flush (no-op if already cached, which also marks it recently used) and schedule the tick. */
  request(key: string): void;
  /** The cached value for a key, or `null` when fetched-with-no-status or not yet fetched. */
  get(key: string): V | null;
  /** Subscribe to cache-change notifications; returns an unsubscribe. */
  subscribe(listener: () => void): () => void;
  /**
   * Drop the named keys from the cache (and the pending queue). `request` short-circuits on a cached
   * key, so an evict MUST precede a re-request for the re-request to actually re-fetch; a bare
   * re-request of a still-cached key is a no-op. Untouched keys and subscribers are left alone — the
   * follow-up re-request's flush is what notifies.
   */
  evict(keys: string[]): void;
  /** Drop the whole cache + pending queue (e.g. after a connection change). */
  clear(): void;
}

/**
 * @param fetchBatch resolves every un-cached key needed this tick to a value or `null`.
 * @param schedule defers the flush one tick; defaults to `queueMicrotask` (a test injects a manual scheduler).
 */
export function createBatchCoalescer<V>(
  fetchBatch: (keys: string[]) => Promise<Map<string, V | null>>,
  schedule: (flush: () => void) => void = (flush) => {
    queueMicrotask(flush);
  },
): BatchCoalescer<V> {
  // key → value, or `null` once fetched with no resolvable status. An ABSENT key means "not fetched yet".
  const cache = new Map<string, V | null>();
  let queued = new Set<string>();
  let flushScheduled = false;
  const listeners = new Set<() => void>();

  function notify(): void {
    for (const listener of listeners) {
      listener();
    }
  }

  async function flush(): Promise<void> {
    flushScheduled = false;
    const need = [...queued].filter((key) => !cache.has(key));
    queued = new Set();
    if (need.length === 0) {
      return;
    }
    try {
      const states = await fetchBatch(need);
      for (const key of need) {
        store(key, states.get(key) ?? null);
      }
    } catch {
      // A failed/deferred read (bad key, v2, unreachable) → no badge for these keys, never a thrown card.
      for (const key of need) {
        store(key, null);
      }
    }
    notify();
  }

  // A Map iterates in insertion order, so re-inserting on use makes the first key the least recently used.
  function store(key: string, value: V | null): void {
    cache.delete(key);
    cache.set(key, value);
    while (cache.size > MAX_CACHED_ENTRIES) {
      const oldest = cache.keys().next();
      if (oldest.done) {
        return;
      }
      cache.delete(oldest.value);
    }
  }

  return {
    request(key) {
      if (cache.has(key)) {
        // Touched, not re-fetched: a card scrolled back into view must not become the eviction candidate while
        // it is on screen. Safe to mutate here because `request` is called from an effect, never during render.
        const cached = cache.get(key) ?? null;
        cache.delete(key);
        cache.set(key, cached);
        return;
      }
      queued.add(key);
      if (!flushScheduled) {
        flushScheduled = true;
        schedule(() => {
          void flush();
        });
      }
    },
    evict(keys) {
      for (const key of keys) {
        cache.delete(key);
        queued.delete(key);
      }
    },
    get: (key) => cache.get(key) ?? null,
    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
    clear() {
      cache.clear();
      queued = new Set();
    },
  };
}

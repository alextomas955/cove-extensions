/**
 * Folds every key requested within one scheduler tick into one fetch, so a page of cards costs one
 * request rather than one per card.
 *
 * The invariant this module holds: nothing is retained past the cards that asked for it. Each key is
 * held for as long as at least one caller holds its release, and the entry is dropped when the last
 * one lets go. A library here reaches millions of entities, so a cache that outlived the page would
 * grow with how far someone scrolled.
 *
 * Import-free apart from its own relative siblings, so it runs with no environment and no mocks. The
 * injected `fetchBatch` owns the wire shape; a rejection from it resolves every key in that batch to
 * `null`, which is a card with no badge and never a thrown card.
 */

export interface BatchCoalescer<V> {
  /**
   * Queues `key` for the next flush and returns the release for this holder. The entry is dropped
   * once every holder has released it.
   */
  request: (key: string) => () => void;
  /** The value for `key`, or `null` when it was fetched with no answer or has not been fetched. */
  get: (key: string) => V | null;
  /** Whether `key` has been fetched, which is what tells an absent answer from an unfinished read. */
  settled: (key: string) => boolean;
  /** How many keys are held right now. */
  registered: () => number;
  subscribe: (listener: () => void) => () => void;
}

/**
 * @param fetchBatch resolves every not-yet-held key needed this tick to a value or `null`.
 * @param schedule defers the flush one tick; a test injects a manual scheduler in its place.
 */
export function createBatchCoalescer<V>(
  fetchBatch: (keys: string[]) => Promise<Map<string, V | null>>,
  schedule: (flush: () => void) => void = (flush) => {
    queueMicrotask(flush);
  },
): BatchCoalescer<V> {
  const holders = new Map<string, number>();
  const values = new Map<string, V | null>();
  const listeners = new Set<() => void>();
  let queued = new Set<string>();
  let flushScheduled = false;

  function notify(): void {
    for (const listener of listeners) listener();
  }

  async function flush(): Promise<void> {
    flushScheduled = false;
    const need = [...queued].filter((key) => !values.has(key));
    queued = new Set();
    if (need.length === 0) return;

    let answered: Map<string, V | null> | null = null;
    try {
      answered = await fetchBatch(need);
    } catch {
      answered = null;
    }

    for (const key of need) {
      // A key every holder released while the fetch was in flight is not stored, so its card
      // leaves nothing behind.
      if (!holders.has(key)) continue;
      values.set(key, answered?.get(key) ?? null);
    }
    notify();
  }

  return {
    request(key) {
      holders.set(key, (holders.get(key) ?? 0) + 1);
      if (!values.has(key)) {
        queued.add(key);
        if (!flushScheduled) {
          flushScheduled = true;
          schedule(() => {
            void flush();
          });
        }
      }

      let released = false;
      return () => {
        if (released) return;
        released = true;
        const held = (holders.get(key) ?? 1) - 1;
        if (held > 0) {
          holders.set(key, held);
          return;
        }
        holders.delete(key);
        values.delete(key);
        queued.delete(key);
      };
    },

    get: (key) => values.get(key) ?? null,
    settled: (key) => values.has(key),
    registered: () => holders.size,

    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
  };
}

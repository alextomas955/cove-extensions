/**
 * Folds every key requested within one scheduler tick into as few fetches as the server answers for,
 * so a page of cards costs one request per answered page rather than one per card.
 *
 * Nothing is retained past the cards that asked for it: a key is held while at least one caller
 * holds its release, and the entry is dropped when the last one lets go. A library here reaches
 * millions of entities, so a cache that outlived the page would grow with how far someone scrolled.
 *
 * The injected `fetchBatch` owns the wire shape. A rejection from it resolves every key still
 * waiting to `null`, which is a card with no badge and never a thrown card.
 */

export interface FetchedBatch<V> {
  /** One entry per key answered for. A key absent here was not answered, which is not the same as answered with nothing. */
  readonly answers: Map<string, V | null>;
  /** Whether the fetch was given more keys than one page answers for, so the rest are worth asking again. */
  readonly moreNotAnswered: boolean;
}

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
  /** Every answer for a key still held, in no particular order. */
  answered: () => (V | null)[];
  /**
   * Drops the answers held for `keys` and fetches them again.
   *
   * For a caller that changed what those answers describe. A key no holder is left for is skipped:
   * nothing on screen would draw it.
   */
  reread: (keys: readonly string[]) => void;
  subscribe: (listener: () => void) => () => void;
}

/**
 * @param fetchBatch answers as many of the keys it is given as one page holds, and says whether it
 * was given more. Its second argument is true only when no key this coalescer holds had an answer as
 * the fetch began. The next fetch of the same flush, and the first fetch of a key requested while
 * one is in flight, are both told false. How many keys one page holds is the server's figure and is
 * never held here.
 * @param schedule defers the flush one tick; a test injects a manual scheduler in its place.
 */
export function createBatchCoalescer<V>(
  fetchBatch: (keys: string[], noAnswersHeld: boolean) => Promise<FetchedBatch<V>>,
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

    // One fetch at a time: the reads behind each one are sequential against a third party.
    let sending = need;
    while (sending.length > 0) {
      let answered: FetchedBatch<V> | null = null;
      try {
        answered = await fetchBatch(sending, values.size === 0);
      } catch {
        answered = null;
      }

      // What the fetch could not answer for is asked again only where it said there was more. A
      // fetch that answered nothing and claimed no remainder has said all it can.
      const again = new Set(
        answered?.moreNotAnswered === true
          ? sending.filter((key) => !answered.answers.has(key))
          : [],
      );

      for (const key of sending) {
        // A key every holder released while the fetch was in flight is not stored.
        if (!holders.has(key) || again.has(key)) continue;
        values.set(key, answered?.answers.get(key) ?? null);
      }
      notify();

      sending = [...again];
    }
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
    answered: () => [...values].filter(([key]) => holders.has(key)).map(([, value]) => value),

    reread(keys) {
      const held = keys.filter((key) => holders.has(key));
      if (held.length === 0) return;

      for (const key of held) {
        values.delete(key);
        queued.add(key);
      }

      // Emitted before the fetch, so a card draws as unsettled rather than holding the answer the
      // action just made stale.
      notify();

      if (!flushScheduled) {
        flushScheduled = true;
        schedule(() => {
          void flush();
        });
      }
    },

    subscribe(listener) {
      listeners.add(listener);
      return () => {
        listeners.delete(listener);
      };
    },
  };
}

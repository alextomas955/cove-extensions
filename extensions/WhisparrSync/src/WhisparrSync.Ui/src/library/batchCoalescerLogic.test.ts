/**
 * The properties the coalescer exists to hold, each driven with an injected scheduler and an injected
 * fetch so no environment is involved.
 *
 * Two are the point of the module. An entry is dropped when its last holder releases, so nothing is
 * retained past the cards that asked for it: a library here reaches millions of entities, and a cache
 * that outlived the page would grow with how far someone scrolled. And a tick holding more keys than
 * one fetch may carry is split rather than sent whole, because the number of cards that mount at once
 * is the host page's own size.
 */
import { expect, test } from "vitest";

import { createBatchCoalescer } from "./batchCoalescerLogic";

/** The bound this test hands the coalescer, standing for the one the route enforces. */
const PER_FETCH = 40;

/** A scheduler under the test's own control, so a flush happens where the test says it does. */
function manualScheduler(): { schedule: (flush: () => void) => void; run: () => Promise<void> } {
  const pending: (() => void)[] = [];
  return {
    schedule: (flush) => {
      pending.push(flush);
    },
    run: async () => {
      while (pending.length > 0) pending.shift()!();
      // The flush itself is async, so the queued promise has to drain before its result is read.
      await Promise.resolve();
      await Promise.resolve();
    },
  };
}

/** A fetch that answers every key it is given, counting the calls it took. */
function answering(value: string) {
  const calls: string[][] = [];
  return {
    calls,
    fetchBatch: (keys: string[]) => {
      calls.push([...keys]);
      return Promise.resolve(new Map(keys.map((key) => [key, value])));
    },
  };
}

test("a page of keys requested in one tick costs exactly one fetch", async () => {
  const scheduler = manualScheduler();
  const fetching = answering("held");
  const coalescer = createBatchCoalescer(fetching.fetchBatch, PER_FETCH, scheduler.schedule);

  const keys = Array.from({ length: PER_FETCH }, (_, index) => String(index + 1));
  for (const key of keys) coalescer.request(key);
  await scheduler.run();

  // Counted on the injected function rather than read out of the module, so what is asserted is the
  // number of requests a page actually costs.
  expect(fetching.calls, "a page of cards did not fold into one request").toHaveLength(1);
  expect(fetching.calls[0]).toHaveLength(PER_FETCH);
  expect(coalescer.get("1")).toBe("held");
});

test("a key already held is not asked about again", async () => {
  const scheduler = manualScheduler();
  const fetching = answering("held");
  const coalescer = createBatchCoalescer(fetching.fetchBatch, PER_FETCH, scheduler.schedule);

  coalescer.request("7");
  await scheduler.run();
  coalescer.request("7");
  await scheduler.run();

  expect(fetching.calls).toHaveLength(1);
});

test("a rejected fetch leaves every key in that batch with no answer, and throws nothing", async () => {
  const scheduler = manualScheduler();
  const coalescer = createBatchCoalescer<string>(
    () => Promise.reject(new Error("nothing answered")),
    PER_FETCH,
    scheduler.schedule,
  );

  let told = 0;
  coalescer.subscribe(() => {
    told += 1;
  });

  coalescer.request("1");
  coalescer.request("2");
  await scheduler.run();

  expect(coalescer.get("1"), "a failed read left a value behind").toBeNull();
  expect(coalescer.get("2")).toBeNull();
  expect(
    coalescer.settled("1"),
    "a failed read never settled, so its card reads as still loading",
  ).toBe(true);
  expect(told, "a failed read notified nobody, so the cards never left their loading state").toBe(
    1,
  );
});

test("the last release drops the entry, so nothing is held between pages", async () => {
  const scheduler = manualScheduler();
  const fetching = answering("held");
  const coalescer = createBatchCoalescer(fetching.fetchBatch, PER_FETCH, scheduler.schedule);

  const first = coalescer.request("9");
  const second = coalescer.request("9");
  await scheduler.run();

  first();
  expect(coalescer.settled("9"), "one of two holders let go and the entry went with it").toBe(true);
  expect(coalescer.registered()).toBe(1);

  second();
  expect(coalescer.registered(), "the last holder let go and the key stayed registered").toBe(0);
  expect(coalescer.settled("9"), "the last holder let go and the value stayed behind").toBe(false);

  coalescer.request("9");
  await scheduler.run();

  expect(
    fetching.calls,
    "a key requested again after its entry was dropped was not re-read",
  ).toHaveLength(2);
});

test("a tick holding more keys than one fetch may carry answers every one of them", async () => {
  const scheduler = manualScheduler();
  const fetching = answering("held");
  const coalescer = createBatchCoalescer(fetching.fetchBatch, PER_FETCH, scheduler.schedule);

  // A host page size above the bound, which is a size the list pages offer and remember.
  const keys = Array.from({ length: PER_FETCH * 2 + 1 }, (_, index) => String(index + 1));
  for (const key of keys) coalescer.request(key);
  await scheduler.run();

  // Counted on the injected function, so what is asserted is what the server would have been sent.
  expect(fetching.calls, "a page over the bound was not split").toHaveLength(3);
  for (const sent of fetching.calls) {
    expect(sent.length, "one fetch carried more than the bound allows").toBeLessThanOrEqual(
      PER_FETCH,
    );
  }

  // Every key, not a count: a bound applied by dropping keys would agree with a count of fetches.
  expect([...fetching.calls.flat()].sort()).toEqual([...keys].sort());
  const unanswered = keys.filter((key) => coalescer.get(key) !== "held");
  expect(unanswered, "a card past the bound was left with no answer").toEqual([]);
});

test("a fetch that rejects leaves the keys after it still asked about", async () => {
  const scheduler = manualScheduler();
  const sent: string[][] = [];
  const coalescer = createBatchCoalescer<string>(
    (keys) => {
      sent.push([...keys]);
      return sent.length === 1
        ? Promise.reject(new Error("nothing answered"))
        : Promise.resolve(new Map(keys.map((key) => [key, "held"])));
    },
    PER_FETCH,
    scheduler.schedule,
  );

  const keys = Array.from({ length: PER_FETCH + 1 }, (_, index) => String(index + 1));
  for (const key of keys) coalescer.request(key);
  await scheduler.run();

  expect(sent, "one fetch rejecting took the rest of the page with it").toHaveLength(2);
  expect(coalescer.get(String(PER_FETCH + 1))).toBe("held");
  expect(coalescer.settled("1"), "a rejected fetch left its own keys unsettled").toBe(true);
});

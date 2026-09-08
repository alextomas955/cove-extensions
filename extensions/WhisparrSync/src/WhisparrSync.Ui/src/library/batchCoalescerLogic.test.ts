/**
 * The four properties the coalescer exists to hold, each driven with an injected scheduler and an
 * injected fetch so no environment is involved.
 *
 * The last one is the point of the module: an entry is dropped when its last holder releases, so
 * nothing is retained past the cards that asked for it. A library here reaches millions of entities,
 * and a cache that outlived the page would grow with how far someone scrolled.
 */
import { expect, test } from "vitest";

import { createBatchCoalescer } from "./batchCoalescerLogic";

/** A page's worth of cards, which is the bound the route enforces on one body. */
const ONE_PAGE = 40;

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
  const coalescer = createBatchCoalescer(fetching.fetchBatch, scheduler.schedule);

  const keys = Array.from({ length: ONE_PAGE }, (_, index) => String(index + 1));
  for (const key of keys) coalescer.request(key);
  await scheduler.run();

  // Counted on the injected function rather than read out of the module, so what is asserted is the
  // number of requests a page actually costs.
  expect(fetching.calls, "a page of cards did not fold into one request").toHaveLength(1);
  expect(fetching.calls[0]).toHaveLength(ONE_PAGE);
  expect(coalescer.get("1")).toBe("held");
});

test("a key already held is not asked about again", async () => {
  const scheduler = manualScheduler();
  const fetching = answering("held");
  const coalescer = createBatchCoalescer(fetching.fetchBatch, scheduler.schedule);

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
  const coalescer = createBatchCoalescer(fetching.fetchBatch, scheduler.schedule);

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

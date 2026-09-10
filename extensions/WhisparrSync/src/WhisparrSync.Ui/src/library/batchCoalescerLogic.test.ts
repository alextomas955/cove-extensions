/**
 * The properties the coalescer exists to hold, each driven with an injected scheduler and an injected
 * fetch so no environment is involved.
 *
 * Two are the point of the module. An entry is dropped when its last holder releases, so nothing is
 * retained past the cards that asked for it: a library here reaches millions of entities, and a cache
 * that outlived the page would grow with how far someone scrolled. And a tick holding more keys than
 * the server answers for in one page reaches every one of them without the coalescer holding a page
 * size of its own, so the fake below is what decides how many one answer carries.
 */
import { expect, test } from "vitest";

import { createBatchCoalescer, type FetchedBatch } from "./batchCoalescerLogic";

/** A scheduler under the test's own control, so a flush happens where the test says it does. */
function manualScheduler(): { schedule: (flush: () => void) => void; run: () => Promise<void> } {
  const pending: (() => void)[] = [];
  return {
    schedule: (flush) => {
      pending.push(flush);
    },
    run: async () => {
      while (pending.length > 0) pending.shift()!();
      // The flush itself is async and each answered page awaits the next, so the queued promises
      // have to drain repeatedly before the result is read.
      for (let turn = 0; turn < 50; turn += 1) await Promise.resolve();
    },
  };
}

/**
 * A fetch that answers at most `perPage` of the keys it is given and says when it was given more,
 * which is what the route does. The page size lives here rather than in the coalescer, so a
 * coalescer tuned to one figure fails against another.
 */
function answering(value: string, perPage = Number.MAX_SAFE_INTEGER) {
  const calls: string[][] = [];
  return {
    calls,
    fetchBatch: (keys: string[]): Promise<FetchedBatch<string>> => {
      calls.push([...keys]);
      const answered = keys.slice(0, perPage);
      return Promise.resolve({
        answers: new Map(answered.map((key) => [key, value])),
        moreNotAnswered: answered.length < keys.length,
      });
    },
  };
}

test("a page of keys requested in one tick costs exactly one fetch", async () => {
  const scheduler = manualScheduler();
  const fetching = answering("held");
  const coalescer = createBatchCoalescer(fetching.fetchBatch, scheduler.schedule);

  const keys = Array.from({ length: 40 }, (_, index) => String(index + 1));
  for (const key of keys) coalescer.request(key);
  await scheduler.run();

  // Counted on the injected function rather than read out of the module, so what is asserted is the
  // number of requests a page actually costs.
  expect(fetching.calls, "a page of cards did not fold into one request").toHaveLength(1);
  expect(fetching.calls[0]).toHaveLength(keys.length);
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

test("a rejected fetch leaves every key waiting on it with no answer, and throws nothing", async () => {
  const scheduler = manualScheduler();
  const asked: string[][] = [];
  const coalescer = createBatchCoalescer<string>((keys) => {
    asked.push([...keys]);
    return Promise.reject(new Error("nothing answered"));
  }, scheduler.schedule);

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
  expect(asked, "a rejected fetch was sent again with the same keys").toHaveLength(1);
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

/**
 * Two page sizes, because the property is that every card is reached whatever the server answers
 * for. A coalescer carrying a figure of its own would pass at one size and fail at the other.
 */
test.for([
  { perPage: 40, count: 81, fetches: 3 },
  { perPage: 7, count: 20, fetches: 3 },
])(
  "every key is answered where the server answers $perPage at a time",
  async ({ perPage, count, fetches }) => {
    const scheduler = manualScheduler();
    const fetching = answering("held", perPage);
    const coalescer = createBatchCoalescer(fetching.fetchBatch, scheduler.schedule);

    const keys = Array.from({ length: count }, (_, index) => String(index + 1));
    for (const key of keys) coalescer.request(key);
    await scheduler.run();

    // Counted on the injected function, so what is asserted is what the server was actually sent.
    expect(fetching.calls, "the remainder was not asked about again").toHaveLength(fetches);

    // Every key, not a count: a client that dropped the remainder would agree with a count of
    // fetches.
    const unanswered = keys.filter((key) => coalescer.get(key) !== "held");
    expect(unanswered, "a card past the first page was left with no answer").toEqual([]);
  },
);

test("a server answering nothing and naming no remainder is not asked again", async () => {
  const scheduler = manualScheduler();
  const asked: string[][] = [];
  const coalescer = createBatchCoalescer<string>((keys) => {
    asked.push([...keys]);
    return Promise.resolve({ answers: new Map<string, string | null>(), moreNotAnswered: false });
  }, scheduler.schedule);

  coalescer.request("1");
  coalescer.request("2");
  await scheduler.run();

  // A refused page answers no row and names no remainder. Asking again would loop forever against a
  // server that has already said all it can.
  expect(asked, "a refused page was asked about again").toHaveLength(1);
  expect(coalescer.settled("1"), "a refused card reads as still loading").toBe(true);
  expect(coalescer.get("1")).toBeNull();
});

test("only a fetch beginning with no answer held is told it holds none", async () => {
  const scheduler = manualScheduler();
  const told: boolean[] = [];
  const fetching = answering("held", 40);
  const coalescer = createBatchCoalescer<string>((keys, noAnswersHeld) => {
    told.push(noAnswersHeld);
    return fetching.fetchBatch(keys);
  }, scheduler.schedule);

  const keys = Array.from({ length: 41 }, (_, index) => String(index + 1));
  const held = keys.map((key) => coalescer.request(key));
  await scheduler.run();

  const late = coalescer.request("999");
  await scheduler.run();

  expect(told, "a fetch that began with answers held was told it held none").toEqual([
    true,
    false,
    false,
  ]);

  for (const release of held) release();
  late();
});

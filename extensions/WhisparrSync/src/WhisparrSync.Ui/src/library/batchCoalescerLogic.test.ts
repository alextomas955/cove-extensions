// The scheduler and the fetch are injected, so no environment is involved. The page size lives in
// the fake below, not in the coalescer.
import { expect, test } from "vitest";

import { createBatchCoalescer, type FetchedBatch } from "./batchCoalescerLogic";

// A scheduler under the test's own control, so a flush happens where the test says it does.
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

// A fetch that answers at most `perPage` keys and says when it was given more, as the route does.
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

  // Counted on the injected function, so what is asserted is the number of requests a page costs.
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

// Two page sizes: a coalescer carrying a figure of its own would pass at one and fail at the other.
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

    // Counted on the injected function, so what is asserted is what the server was sent.
    expect(fetching.calls, "the remainder was not asked about again").toHaveLength(fetches);

    // Every key, not a count: a client that dropped the remainder would still make the right
    // number of fetches.
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

  // A refused page answers no row and names no remainder. Asking again would loop forever.
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

// The run a reader started changes what the answers describe, so the card that drew the old one has
// to be given the new one without the page being loaded again.
test("re-reading fetches every key still on screen and answers it again", async () => {
  const scheduler = manualScheduler();
  const calls: string[][] = [];
  let answer = "before";
  const coalescer = createBatchCoalescer<string>((keys) => {
    calls.push([...keys]);
    return Promise.resolve({
      answers: new Map(keys.map((key) => [key, answer])),
      moreNotAnswered: false,
    });
  }, scheduler.schedule);

  coalescer.request("7");
  coalescer.request("8");
  await scheduler.run();
  expect(coalescer.get("7")).toBe("before");

  answer = "after";
  coalescer.reread(["7", "8"]);
  await scheduler.run();

  expect(calls).toEqual([
    ["7", "8"],
    ["7", "8"],
  ]);
  expect(coalescer.get("7")).toBe("after");
  expect(coalescer.get("8")).toBe("after");
});

// A card drawn from an answer the action has already made wrong is worse than one drawn as
// unsettled: the reader cannot tell it apart from a state the instance really holds.
test("re-reading drops the held answers before the fetch that replaces them", async () => {
  const scheduler = manualScheduler();
  const fetching = answering("held");
  const coalescer = createBatchCoalescer(fetching.fetchBatch, scheduler.schedule);

  coalescer.request("7");
  await scheduler.run();
  expect(coalescer.settled("7")).toBe(true);

  coalescer.reread(["7"]);

  expect(coalescer.settled("7")).toBe(false);
});

// Nothing on screen is nothing to repaint, and a fetch of no keys is a request the route answers
// for no card.
test("re-reading a key no card is holding sends no fetch", async () => {
  const scheduler = manualScheduler();
  const fetching = answering("held");
  const coalescer = createBatchCoalescer(fetching.fetchBatch, scheduler.schedule);

  coalescer.reread(["7"]);
  await scheduler.run();

  expect(fetching.calls).toEqual([]);
});

// One generation reads a card with a request of its own, so repainting a page over one changed card
// would spend a request on every other card for nothing.
test("re-reading names only the keys it was given", async () => {
  const scheduler = manualScheduler();
  const fetching = answering("held");
  const coalescer = createBatchCoalescer(fetching.fetchBatch, scheduler.schedule);

  coalescer.request("7");
  coalescer.request("8");
  coalescer.request("9");
  await scheduler.run();

  coalescer.reread(["8"]);
  await scheduler.run();

  expect(fetching.calls).toEqual([["7", "8", "9"], ["8"]]);
  expect(coalescer.settled("7")).toBe(true);
});

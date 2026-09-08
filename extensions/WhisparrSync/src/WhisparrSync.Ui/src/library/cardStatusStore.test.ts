/**
 * What the store answers before, during and after a batch, and the two facts it holds for the page
 * rather than for a card.
 *
 * The reading is handed back unchanged. Which state a card draws is derived in the browser from
 * those three members, so a store that reshaped them would put a second derivation in front of the
 * one the vocabulary owns.
 *
 * The request goes through the shared extension request, which is replaced here: the module is at
 * module scope and a real one would need a host.
 */
import { afterEach, beforeEach, expect, test, vi } from "vitest";

import type { LibraryStatusView } from "../wire/api";

const requestJson = vi.fn<(path: string, options: unknown) => Promise<LibraryStatusView>>();

vi.mock("@cove-extensions/ui-shared/extensionRequest", () => ({
  requestJson: (path: string, options: unknown): Promise<LibraryStatusView> =>
    requestJson(path, options),
}));

const {
  cardStatusRefusal,
  cardStatusSettled,
  readCardStatus,
  registeredCardCount,
  requestCardStatus,
  subscribeCardStatus,
} = await import("./cardStatusStore");

/** Held so each test leaves the module-scope store empty. */
let releases: (() => void)[] = [];

function register(coveId: number): void {
  releases.push(requestCardStatus("studio", coveId));
}

/** Every identifier one call carried, read off the request the store made. */
function idsSent(call: number): number[] {
  const [, options] = requestJson.mock.calls[call] as unknown as [string, { body: string }];
  return (JSON.parse(options.body) as { coveIds: number[] }).coveIds;
}

/** Lets the coalescer's microtask flush and every request it makes settle. */
async function settle(): Promise<void> {
  for (let turn = 0; turn < 16; turn++) await Promise.resolve();
}

beforeEach(() => {
  requestJson.mockReset();
});

afterEach(() => {
  for (const release of releases) release();
  releases = [];
});

function answering(view: LibraryStatusView): void {
  requestJson.mockResolvedValue(view);
}

test("a card nobody registered has no reading, so the badges are silent until asked for", () => {
  expect(readCardStatus("studio", 1)).toBeNull();
  expect(cardStatusSettled("studio", 1)).toBe(false);
  expect(registeredCardCount()).toBe(0);
});

test("a row carrying no reading answers null, which is a card with no badge", async () => {
  answering({ rows: [{ coveId: 1, reading: null }], refusal: "none" });

  register(1);
  await settle();

  expect(readCardStatus("studio", 1)).toBeNull();
  expect(
    cardStatusSettled("studio", 1),
    "the read answered and the card still reads as loading",
  ).toBe(true);
});

test("a row carrying a reading answers its three members unchanged", async () => {
  const reading = { excluded: false, present: true, monitored: true };
  answering({ rows: [{ coveId: 2, reading }], refusal: "none" });

  register(2);
  await settle();

  expect(readCardStatus("studio", 2)).toEqual(reading);
});

test("one request carries every card registered in the same tick", async () => {
  answering({
    rows: [
      { coveId: 3, reading: { excluded: false, present: false, monitored: false } },
      { coveId: 4, reading: null },
    ],
    refusal: "none",
  });

  register(3);
  register(4);
  await settle();

  expect(requestJson, "two cards on one page cost two requests").toHaveBeenCalledTimes(1);
  expect(registeredCardCount()).toBe(2);

  const [path, options] = requestJson.mock.calls[0] as unknown as [
    string,
    { method: string; body: string },
  ];
  expect(path).toContain("library/studio/status");
  expect(options.method, "reading a status used a method that could change something").toBe("POST");
  expect(JSON.parse(options.body)).toEqual({ coveIds: [3, 4] });
});

test("the page's own reason is readable with no card carrying one", async () => {
  answering({ rows: [{ coveId: 5, reading: null }], refusal: "instanceUnreachable" });

  let told = 0;
  const unsubscribe = subscribeCardStatus(() => {
    told += 1;
  });

  register(5);
  await settle();

  expect(cardStatusRefusal()).toBe("instanceUnreachable");
  expect(readCardStatus("studio", 5), "the reason reached a card as well as the page").toBeNull();
  expect(told, "the reason arrived and nothing was told about it").toBeGreaterThan(0);

  unsubscribe();
});

test("a page holding more cards than one request may carry answers every one of them", async () => {
  // A host page size above the route's bound, which is a size the list pages offer and remember.
  const coveIds = Array.from({ length: 41 }, (_, index) => index + 100);
  requestJson.mockImplementation((_path, options) => {
    const sent = (JSON.parse((options as { body: string }).body) as { coveIds: number[] }).coveIds;
    return Promise.resolve({
      rows: sent.map((coveId) => ({
        coveId,
        reading: { excluded: false, present: true, monitored: true },
      })),
      refusal: "none",
    });
  });

  for (const coveId of coveIds) register(coveId);
  await settle();

  expect(requestJson, "a page over the route's bound was sent as one body").toHaveBeenCalledTimes(
    2,
  );
  for (let call = 0; call < 2; call++) {
    expect(
      idsSent(call).length,
      "one request carried more than the route accepts",
    ).toBeLessThanOrEqual(40);
  }

  // Every card, not a count: a bound applied by dropping identifiers would agree with a count of
  // requests.
  expect([...idsSent(0), ...idsSent(1)].sort()).toEqual([...coveIds].sort());
  const silent = coveIds.filter((coveId) => readCardStatus("studio", coveId) === null);
  expect(silent, "a card past the route's bound drew no badge").toEqual([]);
});

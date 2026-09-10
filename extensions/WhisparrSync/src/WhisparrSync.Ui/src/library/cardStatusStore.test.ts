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

/** One answer for every request. A view naming no remainder is the default, as most tests want. */
function answering(
  view: Partial<LibraryStatusView> & Pick<LibraryStatusView, "rows" | "refusal">,
): void {
  requestJson.mockResolvedValue({ kind: "studio", moreNotAnswered: false, ...view });
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

test("a request that never answered is stated on the page rather than left silent", async () => {
  requestJson.mockRejectedValue(new Error("the route refused the body"));

  let told = 0;
  const unsubscribe = subscribeCardStatus(() => {
    told += 1;
  });

  register(6);
  await settle();

  expect(cardStatusRefusal(), "a failed request left the page with no reason at all").toBe(
    "statusCouldNotBeRead",
  );
  expect(readCardStatus("studio", 6), "a failed request left a reading behind").toBeNull();
  expect(cardStatusSettled("studio", 6)).toBe(true);
  expect(told, "the failure arrived and nothing was told about it").toBeGreaterThan(0);

  unsubscribe();
});

test("a retry that failed states its own reason rather than the one before it", async () => {
  answering({ rows: [{ coveId: 7, reading: null }], refusal: "instanceUnreachable" });
  register(7);
  await settle();
  expect(cardStatusRefusal()).toBe("instanceUnreachable");

  // The card that read leaves the screen, so the reason describes nothing still on it.
  for (const release of releases) release();
  releases = [];

  requestJson.mockRejectedValue(new Error("Cove answered nothing"));
  register(8);
  await settle();

  expect(cardStatusRefusal(), "the page kept stating the reason from the batch before it").toBe(
    "statusCouldNotBeRead",
  );
});

test("a page whose cards all answered states no reason after a page that failed", async () => {
  requestJson.mockRejectedValue(new Error("nothing answered"));
  register(500);
  await settle();
  expect(cardStatusRefusal()).toBe("statusCouldNotBeRead");

  // The reader turned the page, so every card the failed read covered left the screen.
  for (const release of releases) release();
  releases = [];

  answering({
    rows: [{ coveId: 501, reading: { excluded: false, present: true, monitored: true } }],
    refusal: "none",
  });
  register(501);
  await settle();

  expect(readCardStatus("studio", 501)).not.toBeNull();
  expect(cardStatusRefusal(), "the new page carried the previous page's reason").toBe("none");
});

/**
 * A page holding more cards than the route answers for in one go. The route answers a page and says
 * there is more; the store asks again for what came back with no row. How many one page holds is the
 * route's own figure and is never held here, so the fake below is what decides it.
 */
function truncatingAt(perPage: number, refusal: LibraryStatusView["refusal"] = "none") {
  return (_path: string, options: unknown) => {
    const asked = (JSON.parse((options as { body: string }).body) as { coveIds: number[] }).coveIds;
    const answered = asked.slice(0, perPage);
    const view: LibraryStatusView = {
      kind: "studio",
      rows: answered.map((coveId) => ({
        coveId,
        reading: { excluded: false, present: true, monitored: true },
      })),
      refusal,
      moreNotAnswered: answered.length < asked.length,
    };
    return Promise.resolve(view);
  };
}

test("a page holding more cards than one answer carries reaches every one of them", async () => {
  // A host page size above what the route answers for, which is a size the list pages offer and
  // remember.
  const coveIds = Array.from({ length: 41 }, (_, index) => index + 100);
  requestJson.mockImplementation(truncatingAt(40));

  for (const coveId of coveIds) register(coveId);
  await settle();

  expect(
    requestJson,
    "the cards the route left unanswered were never asked about again",
  ).toHaveBeenCalledTimes(2);

  // The store sends what it holds and never a page of its own, so the first body carries all of
  // them and the second only what came back with no row.
  expect(idsSent(0)).toEqual(coveIds);
  expect(idsSent(1)).toEqual([coveIds[40]]);

  const silent = coveIds.filter((coveId) => readCardStatus("studio", coveId) === null);
  expect(silent, "a card the route did not answer for first time round drew no badge").toEqual([]);
  expect(cardStatusRefusal(), "a page whose every card answered stated a reason").toBe("none");
});

test("a reason the instance established is not displaced by a request that never answered", async () => {
  // One page, two requests, two reasons. The page states one sentence, and it is the first reason
  // established: the reason changing under the reader as later requests answer would say the page
  // had been read twice.
  const coveIds = Array.from({ length: 41 }, (_, index) => index + 600);
  const first = truncatingAt(40, "instanceUnreachable");
  let sent = 0;
  requestJson.mockImplementation((path, options) => {
    sent += 1;
    return sent === 1
      ? first(path, options)
      : Promise.reject(new Error("the route answered nothing"));
  });

  for (const coveId of coveIds) register(coveId);
  await settle();

  expect(requestJson).toHaveBeenCalledTimes(2);
  expect(cardStatusRefusal(), "the browser's own reason displaced the one the instance gave").toBe(
    "instanceUnreachable",
  );
});

test("a card that mounts while a failed page is still on screen leaves its reason standing", async () => {
  // Cove mounts a card on a scroll, so a card is asked about on its own while the cards of a request
  // that never answered are still on screen drawing nothing. The page still has its reason.
  const coveIds = Array.from({ length: 41 }, (_, index) => index + 300);
  requestJson.mockRejectedValue(new Error("the route answered nothing"));

  for (const coveId of coveIds) register(coveId);
  await settle();
  expect(cardStatusRefusal(), "the failed request established no reason to hold").toBe(
    "statusCouldNotBeRead",
  );

  requestJson.mockImplementation(truncatingAt(40));
  register(341);
  await settle();

  expect(
    readCardStatus("studio", 341),
    "the card that mounted was not asked about on its own",
  ).not.toBeNull();
  expect(
    coveIds.filter((coveId) => readCardStatus("studio", coveId) !== null),
    "a card the failed request covered drew a badge after all",
  ).toEqual([]);
  expect(
    cardStatusRefusal(),
    "the card that mounted mid-read took away the reason the page had established",
  ).toBe("statusCouldNotBeRead");
});

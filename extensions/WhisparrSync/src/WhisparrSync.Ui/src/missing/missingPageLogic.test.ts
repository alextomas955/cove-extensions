import { describe, expect, it } from "vitest";

import {
  clampToReachable,
  lastReachablePage,
  pageIsReachable,
  pagerTotalFor,
} from "./missingPageLogic";

/**
 * Paging measured against the live providers on 2026-09-06.
 *
 * ThePornDB serves at most ten thousand rows for any query and reports a total of `min(actual,
 * 10000)`, so at the ceiling its reported size is a floor while the set behind it is larger.
 */
const RECORDED = {
  /** `per_page=40&page=1` against tag 70: total 10,000, last_page 250. Page 251 re-serves page 250. */
  thePornDbAtTheCeiling: { catalogueSize: 10000, lastPage: 250, perPage: 40 },
  /** `site_id=92`: 272 scenes, last_page 7, and page 8 answers no rows at all. */
  thePornDbBelowTheCeiling: { catalogueSize: 272, lastPage: 7, perPage: 40 },
  /** StashDB against a 162,350-scene tag at forty a page, with a genuine short last page. */
  stashDb: { catalogueSize: 162350, lastPage: 4059, perPage: 40 },
} as const;

describe("the pager is sized from what the provider will serve", () => {
  it("counts the pages the provider stops at, not the catalogue it reports", () => {
    expect(pagerTotalFor(RECORDED.thePornDbBelowTheCeiling)).toBe(280);
    expect(pagerTotalFor(RECORDED.thePornDbBelowTheCeiling)).not.toBe(
      RECORDED.thePornDbBelowTheCeiling.catalogueSize,
    );
  });

  it("answers the same for a catalogue size an order of magnitude apart", () => {
    const bounds = RECORDED.thePornDbAtTheCeiling;

    // The reported size at the ceiling is a floor: the set behind it is larger and unknowable from
    // the response. Nothing the pager is given may move with it.
    expect(pagerTotalFor({ lastPage: bounds.lastPage, perPage: bounds.perPage })).toBe(10000);
    expect(pagerTotalFor(bounds)).toBe(10000);
  });

  it("sizes a provider with no ceiling from its own last page", () => {
    expect(pagerTotalFor(RECORDED.stashDb)).toBe(162360);
  });
});

describe("no page the pager offers repeats another", () => {
  it("offers the provider's last page and nothing past it", () => {
    const bounds = RECORDED.thePornDbAtTheCeiling;

    expect(lastReachablePage(bounds)).toBe(250);
    expect(pageIsReachable(250, bounds)).toBe(true);
    // 251 answered 200 with the rows of page 250 and reported `current_page` as 250.
    expect(pageIsReachable(251, bounds)).toBe(false);
    expect(pageIsReachable(400, bounds)).toBe(false);
  });

  it("refuses a page below the first and a page that is not whole", () => {
    const bounds = RECORDED.stashDb;

    expect(pageIsReachable(0, bounds)).toBe(false);
    expect(pageIsReachable(-1, bounds)).toBe(false);
    expect(pageIsReachable(1.5, bounds)).toBe(false);
    expect(pageIsReachable(1, bounds)).toBe(true);
    expect(pageIsReachable(4059, bounds)).toBe(true);
    expect(pageIsReachable(4064, bounds)).toBe(false);
  });

  it("brings a page from a shared link inside the range rather than asking for it", () => {
    const bounds = RECORDED.thePornDbAtTheCeiling;

    expect(clampToReachable(10002, bounds)).toBe(250);
    expect(clampToReachable(0, bounds)).toBe(1);
    expect(clampToReachable(7, bounds)).toBe(7);
  });

  it("keeps a first page for a catalogue the provider reports no page for", () => {
    const bounds = { lastPage: 0, perPage: 40 };

    expect(lastReachablePage(bounds)).toBe(1);
    expect(pageIsReachable(1, bounds)).toBe(true);
    expect(pageIsReachable(2, bounds)).toBe(false);
  });
});

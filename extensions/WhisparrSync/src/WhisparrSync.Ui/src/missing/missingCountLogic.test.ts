import { describe, expect, it } from "vitest";

import { COUNT_IS_THE_CATALOGUE_SIZE, countLine, selectionCount } from "../common/ui/copy";
import { catalogueSizeLabel, ceilingIsDisclosed, countLineParts } from "./missingCountLogic";

/**
 * Provider responses recorded against the live services on 2026-09-06, transcribed rather than
 * invented. The ceiling case cannot be produced by hand: it needs a set larger than the ten thousand
 * rows ThePornDB will serve.
 */
const RECORDED = {
  /** ThePornDB, tag 70, forty a page: the reported total is a floor and page 251 re-serves page 250. */
  thePornDbAtTheCeiling: {
    rangeFrom: 1,
    rangeTo: 40,
    catalogueSize: 10000,
    sizeIsLowerBound: true,
  },
  /** ThePornDB, site 92, forty a page: 272 scenes, below the ceiling, so the total is exact. */
  thePornDbBelowTheCeiling: {
    rangeFrom: 1,
    rangeTo: 40,
    catalogueSize: 272,
    sizeIsLowerBound: false,
  },
  /** ThePornDB, site 92 filtered to 2017: one scene in the whole set. */
  oneScene: { rangeFrom: 1, rangeTo: 1, catalogueSize: 1, sizeIsLowerBound: false },
  /** ThePornDB, site 92, the page past the last: no rows and a null `from`. */
  noScenes: { rangeFrom: 0, rangeTo: 0, catalogueSize: 0, sizeIsLowerBound: false },
} as const;

describe("the count line states the provider's figures", () => {
  it("marks a total the provider will not serve past", () => {
    const parts = countLineParts(RECORDED.thePornDbAtTheCeiling);

    expect(parts).toEqual({ from: 1, to: 40, total: 10000, atCeiling: true });
    expect(countLine(parts.from, parts.to, parts.total, parts.atCeiling)).toBe("1–40 of 10000+");
  });

  it("leaves an exact total unmarked", () => {
    const parts = countLineParts(RECORDED.thePornDbBelowTheCeiling);

    expect(parts.atCeiling).toBe(false);
    expect(countLine(parts.from, parts.to, parts.total, parts.atCeiling)).toBe("1–40 of 272");
  });

  it("reads the range from the provider and not from the cards left after the subtraction", () => {
    // Thirty-one of the forty survived; the range is still the provider's own.
    const cardsOnScreen = 31;
    const parts = countLineParts(RECORDED.thePornDbBelowTheCeiling);

    expect(parts.to - parts.from + 1).toBe(40);
    expect(parts.to - parts.from + 1).not.toBe(cardsOnScreen);
  });

  it("renders a catalogue of one", () => {
    const parts = countLineParts(RECORDED.oneScene);

    expect(countLine(parts.from, parts.to, parts.total, parts.atCeiling)).toBe("1–1 of 1");
  });

  it("renders a catalogue of none without inventing a first position", () => {
    const parts = countLineParts(RECORDED.noScenes);

    expect(parts).toEqual({ from: 0, to: 0, total: 0, atCeiling: false });
    expect(countLine(parts.from, parts.to, parts.total, parts.atCeiling)).toBe("0–0 of 0");
  });

  it("does not mark an empty catalogue as a floor", () => {
    expect(countLineParts({ ...RECORDED.noScenes, sizeIsLowerBound: true }).atCeiling).toBe(false);
  });
});

describe("the selection count reads at zero, one and many", () => {
  it("singularises at one", () => {
    expect(selectionCount(0)).toBe("0 selected");
    expect(selectionCount(1)).toBe("1 selected");
    expect(selectionCount(2)).toBe("2 selected");
  });
});

describe("one answer about the ceiling", () => {
  it("agrees with the count line's own mark", () => {
    expect(ceilingIsDisclosed(RECORDED.thePornDbAtTheCeiling)).toBe(true);
    expect(ceilingIsDisclosed(RECORDED.thePornDbBelowTheCeiling)).toBe(false);
  });
});

describe("what the figure counts is said once", () => {
  it("names the provider and the entity the placeholders stand for", () => {
    const label = catalogueSizeLabel("StashDB", "this studio");

    expect(label).toBe(
      "This counts every scene StashDB lists for this studio, not the number you are missing.",
    );
    expect(COUNT_IS_THE_CATALOGUE_SIZE).toContain("{provider}");
  });
});

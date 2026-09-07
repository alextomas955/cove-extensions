import { describe, expect, it } from "vitest";

import type { MissingSortOption } from "../wire/api";
import { MISSING_URL_KEYS, writeMissingView } from "./missingUrlLogic";
import {
  MISSING_TOOLBAR_CONTROLS,
  searchSettleDelayMs,
  sortOptionsFor,
} from "./missingToolbarLogic";

/**
 * The three orderings a v3 provider offers, transcribed by hand. A list read from the module under
 * test would agree with whatever it says and report nothing.
 */
const V3_SORTS: MissingSortOption[] = [
  { value: "DATE-DESC", label: "Newest first" },
  { value: "DATE-ASC", label: "Oldest first" },
  { value: "TITLE-ASC", label: "Title A–Z" },
];

/** The two a generation with no title ordering offers. */
const V2_SORTS: MissingSortOption[] = [
  { value: "date_desc", label: "Newest first" },
  { value: "date_asc", label: "Oldest first" },
];

describe("the ordering menu is the provider's own", () => {
  it("offers exactly the options the page answered", () => {
    expect(sortOptionsFor(V3_SORTS, null).map((row) => row.value)).toEqual([
      "DATE-DESC",
      "DATE-ASC",
      "TITLE-ASC",
    ]);
  });

  it("invents no title ordering for a generation that declares none", () => {
    const labels = sortOptionsFor(V2_SORTS, null).map((row) => row.label);
    expect(labels).toEqual(["Newest first", "Oldest first"]);
  });

  it("offers nothing at all for a page carrying no orderings", () => {
    expect(sortOptionsFor([], null)).toEqual([]);
  });

  it("marks the ordering in force and no other", () => {
    const selected = sortOptionsFor(V3_SORTS, "DATE-ASC").filter((row) => row.selected);
    expect(selected.map((row) => row.value)).toEqual(["DATE-ASC"]);
  });

  it("marks nothing when the page names no ordering", () => {
    expect(sortOptionsFor(V3_SORTS, null).some((row) => row.selected)).toBe(false);
  });
});

describe("no control carries a disabled flag", () => {
  it("names each control plainly, so a name is the only thing a control can be", () => {
    for (const control of MISSING_TOOLBAR_CONTROLS) {
      expect(typeof control).toBe("string");
    }
  });
});

describe("typing settles before the address is rewritten", () => {
  it("waits a positive number of milliseconds", () => {
    expect(searchSettleDelayMs).toBeGreaterThan(0);
    expect(Number.isFinite(searchSettleDelayMs)).toBe(true);
  });
});

describe("an empty search means the whole catalogue", () => {
  it("writes no search key at all", () => {
    const search = writeMissingView("", { q: "", page: 1, sort: null, filters: {} });
    expect(new URLSearchParams(search).has(MISSING_URL_KEYS.q)).toBe(false);
  });
});

import { describe, expect, it } from "vitest";

import type { MissingSortOption } from "../wire/api";
import { MISSING_URL_KEYS, writeMissingView } from "./missingUrlLogic";
import {
  searchSettleDelayMs,
  sortOptionsFor,
  toolbarControlsFor,
  type MissingToolbarControl,
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

describe("a control the entity cannot express is absent", () => {
  it("offers a tag one control fewer than a studio", () => {
    expect(toolbarControlsFor("tag", true)).toHaveLength(
      toolbarControlsFor("studio", true).length - 1,
    );
  });

  it("names no whole-view action on a tag", () => {
    expect(toolbarControlsFor("tag", true)).not.toContain("monitorAll");
  });

  it("names no whole-view action on a page that does not offer one", () => {
    expect(toolbarControlsFor("studio", false)).not.toContain("monitorAll");
  });

  it("answers a plain name for every control, so no control can carry a disabled flag", () => {
    const controls: readonly MissingToolbarControl[] = toolbarControlsFor("studio", true);
    for (const control of controls) {
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

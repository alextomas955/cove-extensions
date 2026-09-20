import { describe, expect, it } from "vitest";

import {
  DEFAULT_MISSING_VIEW,
  MISSING_URL_KEYS,
  readMissingView,
  writeMissingView,
  type MissingView,
} from "./missingUrlLogic";

// The keys the host deletes from the address on every tab change. Transcribed by hand from
// `LIST_URL_MANAGED_KEYS` in the host's own list URL hook; a list read from the host at test time
// would agree with whatever it says and stop reporting a collision.
const LIST_URL_MANAGED_KEYS = [
  "q",
  "page",
  "perPage",
  "sort",
  "direction",
  "sorts",
  "view",
  "viewMode",
  "filters",
  "seed",
  "searchMode",
];

describe("this tab's keys survive arriving on it", () => {
  it("collides with none of the keys the host deletes on a tab change", () => {
    for (const key of Object.values(MISSING_URL_KEYS)) {
      expect(LIST_URL_MANAGED_KEYS, key).not.toContain(key);
    }
  });

  it("has four keys to check", () => {
    expect(Object.values(MISSING_URL_KEYS)).toHaveLength(4);
  });
});

describe("a view survives the round trip", () => {
  it("carries every field back", () => {
    const view: MissingView = {
      q: "beach",
      page: 7,
      sort: "date_desc",
      filters: { performer: "abc-123", year: "2019" },
    };

    expect(readMissingView(writeMissingView("", view))).toEqual(view);
  });

  it("carries a filter value holding the separators it encodes with", () => {
    const view: MissingView = {
      q: "",
      page: 1,
      sort: null,
      filters: { "odd:key": "a,b:c" },
    };

    expect(readMissingView(writeMissingView("", view))).toEqual(view);
  });

  it("reads an address carrying none of its keys as the default view", () => {
    expect(readMissingView("")).toEqual(DEFAULT_MISSING_VIEW);
    expect(readMissingView("?page=3&sort=title")).toEqual(DEFAULT_MISSING_VIEW);
  });
});

describe("a field at its default is absent rather than blank", () => {
  it("writes none of its keys for the default view", () => {
    expect(writeMissingView("", DEFAULT_MISSING_VIEW)).toBe("");
  });

  it("drops a key whose field returns to its default", () => {
    const written = writeMissingView("", { q: "beach", page: 4, sort: "a", filters: { k: "v" } });

    const cleared = writeMissingView(written, DEFAULT_MISSING_VIEW);

    expect(cleared).toBe("");
  });
});

describe("a key this tab does not own is left alone", () => {
  it("preserves an unrecognised key already in the address", () => {
    const written = writeMissingView("tab=whisparr-missing", {
      ...DEFAULT_MISSING_VIEW,
      page: 2,
    });

    expect(new URLSearchParams(written).get("tab")).toBe("whisparr-missing");
    expect(new URLSearchParams(written).get(MISSING_URL_KEYS.page)).toBe("2");
  });
});

describe("a hand-edited address shows the catalogue rather than an error", () => {
  it("reads a page that is not a whole number above zero as the first page", () => {
    for (const raw of ["0", "-3", "2.5", "banana", ""]) {
      expect(readMissingView(`${MISSING_URL_KEYS.page}=${raw}`).page).toBe(1);
    }
  });
});

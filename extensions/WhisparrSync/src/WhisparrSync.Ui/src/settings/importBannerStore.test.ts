import { describe, expect, it } from "vitest";

import type { ImportBannerView } from "../wire/api";
import { createImportBannerStore, INITIAL_IMPORT_BANNER_STATE } from "./importBannerStore";

/** An answer in which no root has refusals outstanding: the genuinely-empty read. */
const NOTHING_OUTSTANDING: ImportBannerView = { roots: [] };

const ONE_ROOT: ImportBannerView = {
  roots: [
    {
      root: "/whisparr-media",
      countSinceLastSuccess: 2,
      newestPaths: [{ path: "/whisparr-media/a.mp4", cause: "notFoundUnderAnyRoot" }],
    },
  ],
};

describe("the state before a read and the state after an empty one", () => {
  // The two must never coincide. An initial value equal to the loaded-and-empty value is how a
  // momentary blank comes to read as a confident report that nothing is wrong.
  it("are different states", () => {
    const store = createImportBannerStore();
    store.beginRead();
    store.loaded(NOTHING_OUTSTANDING);

    expect(INITIAL_IMPORT_BANNER_STATE.view).toBeNull();
    expect(INITIAL_IMPORT_BANNER_STATE.read.reading).toBe(true);
    expect(store.getSnapshot().view).not.toBeNull();
    expect(store.getSnapshot().read.reading).toBe(false);
  });
});

describe("the read transitions", () => {
  it("is reading with no content on the first read", () => {
    const store = createImportBannerStore();
    store.beginRead();

    expect(store.getSnapshot().read).toEqual({ reading: true, failed: false, hasContent: false });
    expect(store.getSnapshot().readError).toBeNull();
  });

  it("holds the answer once it lands", () => {
    const store = createImportBannerStore();
    store.beginRead();
    store.loaded(ONE_ROOT);

    expect(store.getSnapshot().view).toEqual(ONE_ROOT);
    expect(store.getSnapshot().read).toEqual({ reading: false, failed: false, hasContent: true });
  });

  it("reports a first read that failed, with nothing to show behind it", () => {
    const store = createImportBannerStore();
    store.beginRead();
    store.readFailed("500 nope");

    expect(store.getSnapshot().view).toBeNull();
    expect(store.getSnapshot().read).toEqual({ reading: false, failed: true, hasContent: false });
    expect(store.getSnapshot().readError).toBe("500 nope");
  });

  it("keeps what it last held when a re-read fails", () => {
    const store = createImportBannerStore();
    store.beginRead();
    store.loaded(ONE_ROOT);
    store.beginRead();
    store.readFailed("500 nope");

    expect(store.getSnapshot().view).toEqual(ONE_ROOT);
    expect(store.getSnapshot().read).toEqual({ reading: false, failed: true, hasContent: true });
  });

  it("replaces the answer wholly rather than merging into it", () => {
    const store = createImportBannerStore();
    store.beginRead();
    store.loaded(ONE_ROOT);
    store.beginRead();
    store.loaded(NOTHING_OUTSTANDING);

    expect(store.getSnapshot().view).toEqual(NOTHING_OUTSTANDING);
    expect(store.getSnapshot().readError).toBeNull();
  });
});

describe("the subscription", () => {
  it("tells a listener about every emit, and stops after it unsubscribes", () => {
    const store = createImportBannerStore();
    let heard = 0;
    const stop = store.subscribe(() => {
      heard += 1;
    });

    store.beginRead();
    store.loaded(ONE_ROOT);
    stop();
    store.beginRead();

    expect(heard).toBe(2);
  });
});

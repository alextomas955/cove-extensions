/**
 * What the catalogue store does with an answer that arrives after the reader has moved on.
 *
 * The settle guard is keyed on the entity AND the page. Keyed on the entity alone, a page-three read
 * settling after a move to page four paints page three's cards under page four's address, with
 * nothing on screen saying so.
 */
import { describe, expect, it } from "vitest";

import { createMissingStore, INITIAL_MISSING_STATE, type MissingViewKey } from "./missingStore";

const STUDIO = { kind: "studio", coveId: 7 } as const;
const OTHER_STUDIO = { kind: "studio", coveId: 8 } as const;

const pageOf = (page: number): MissingViewKey => ({ page, sort: null, q: "", filters: "" });

/** A page carrying one card, named so a settle can be told apart from another. */
const answerFor = (page: number) =>
  ({
    cards: [{ providerSceneId: `scene-on-page-${String(page)}` }],
    page,
  }) as never;

describe("createMissingStore", () => {
  it("starts with no answer rather than an empty one", () => {
    const store = createMissingStore();

    expect(store.getSnapshot()).toEqual(INITIAL_MISSING_STATE);
    expect(store.getSnapshot().view).toBeNull();
    expect(store.getSnapshot().read.reading).toBe(true);
  });

  it("keeps an answer for the page it is still waiting on", () => {
    const store = createMissingStore();
    store.mounted(STUDIO);

    store.beginRead(STUDIO, pageOf(3));
    store.loaded(STUDIO, pageOf(3), answerFor(3));

    expect(store.getSnapshot().view).toEqual(answerFor(3));
  });

  it("drops a settle for a page the reader has already left", () => {
    const store = createMissingStore();
    store.mounted(STUDIO);

    store.beginRead(STUDIO, pageOf(3));
    store.beginRead(STUDIO, pageOf(4));

    // Page three answers late, after page four was asked for.
    store.loaded(STUDIO, pageOf(3), answerFor(3));

    expect(store.getSnapshot().view).toBeNull();

    store.loaded(STUDIO, pageOf(4), answerFor(4));
    expect(store.getSnapshot().view).toEqual(answerFor(4));
  });

  it("drops a failure for a superseded page, so it cannot fail a read that is still running", () => {
    const store = createMissingStore();
    store.mounted(STUDIO);

    store.beginRead(STUDIO, pageOf(3));
    store.beginRead(STUDIO, pageOf(4));
    store.readFailed(STUDIO, pageOf(3));

    expect(store.getSnapshot().read.failed).toBe(false);
    expect(store.getSnapshot().read.reading).toBe(true);
  });

  it("drops a settle for an entity that is no longer on screen", () => {
    const store = createMissingStore();
    store.mounted(STUDIO);
    store.beginRead(STUDIO, pageOf(1));

    store.mounted(OTHER_STUDIO);
    store.loaded(STUDIO, pageOf(1), answerFor(1));

    expect(store.getSnapshot().view).toBeNull();
  });

  it("resets to no answer when a different entity mounts", () => {
    const store = createMissingStore();
    store.mounted(STUDIO);
    store.beginRead(STUDIO, pageOf(1));
    store.loaded(STUDIO, pageOf(1), answerFor(1));

    store.mounted(OTHER_STUDIO);

    expect(store.getSnapshot()).toEqual(INITIAL_MISSING_STATE);
  });

  /** A read in flight over content keeps the content, so the grid never blanks between pages. */
  it("keeps the previous page on screen while the next one is read", () => {
    const store = createMissingStore();
    store.mounted(STUDIO);
    store.beginRead(STUDIO, pageOf(1));
    store.loaded(STUDIO, pageOf(1), answerFor(1));

    store.beginRead(STUDIO, pageOf(2));

    expect(store.getSnapshot().read.hasContent).toBe(true);
    expect(store.getSnapshot().view).toEqual(answerFor(1));
  });

  it("notifies every subscriber and stops on unsubscribe", () => {
    const store = createMissingStore();
    let seen = 0;
    const stop = store.subscribe(() => {
      seen++;
    });

    store.mounted(STUDIO);
    expect(seen).toBe(1);

    stop();
    store.beginRead(STUDIO, pageOf(1));
    expect(seen).toBe(1);
  });
});

describe("what the selection's own verb leaves behind", () => {
  it("holds an outcome and no collection of identifiers", () => {
    const store = createMissingStore();
    store.mounted(STUDIO);

    store.beginBulk(STUDIO);
    store.bulkSettled(STUDIO, { kind: "started" });

    const { bulk } = store.getSnapshot();
    expect(bulk).toEqual({ kind: "started" });
    for (const value of Object.values(bulk)) {
      expect(Array.isArray(value), "the bulk field carries a collection").toBe(false);
    }
  });

  it("states the refusal a press was answered with", () => {
    const store = createMissingStore();
    store.mounted(STUDIO);

    store.bulkSettled(STUDIO, { kind: "refused", refusal: "noInstanceConnected" });

    expect(store.getSnapshot().bulk).toEqual({
      kind: "refused",
      refusal: "noInstanceConnected",
    });
  });

  it("drops what the last press produced when the page under it changes", () => {
    const store = createMissingStore();
    store.mounted(STUDIO);
    store.beginRead(STUDIO, pageOf(1));
    store.loaded(STUDIO, pageOf(1), answerFor(1));
    store.bulkSettled(STUDIO, { kind: "refused", refusal: "notStarted" });

    store.beginRead(STUDIO, pageOf(2));
    store.loaded(STUDIO, pageOf(2), answerFor(2));

    expect(store.getSnapshot().bulk).toEqual({ kind: "atRest" });
  });

  it("ignores an answer for an entity the reader has already left", () => {
    const store = createMissingStore();
    store.mounted(STUDIO);
    store.mounted(OTHER_STUDIO);

    store.bulkSettled(STUDIO, { kind: "started" });

    expect(store.getSnapshot().bulk).toEqual({ kind: "atRest" });
  });
});

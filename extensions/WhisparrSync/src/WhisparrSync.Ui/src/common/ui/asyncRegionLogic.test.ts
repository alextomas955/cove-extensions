import { describe, expect, it } from "vitest";

import { deriveAsyncRegionState, INITIAL_ASYNC_READ, type AsyncRead } from "./asyncRegionLogic";

const read = (over: Partial<AsyncRead>): AsyncRead => ({
  reading: false,
  failed: false,
  hasContent: false,
  ...over,
});

describe("a surface still reading", () => {
  it("reads as reading rather than as empty when it has nothing yet", () => {
    expect(deriveAsyncRegionState(read({ reading: true }))).toEqual({
      status: "reading",
      outage: false,
    });
  });

  it("keeps content it already has rather than blanking for a refresh", () => {
    expect(deriveAsyncRegionState(read({ reading: true, hasContent: true }))).toEqual({
      status: "content",
      outage: false,
    });
  });
});

describe("a read that finished", () => {
  it("tells a genuinely empty answer apart from a failed one", () => {
    const empty = deriveAsyncRegionState(read({}));
    const failed = deriveAsyncRegionState(read({ failed: true }));
    expect(empty.status).toBe("empty");
    expect(failed.status).toBe("failed");
    expect(empty).not.toEqual(failed);
  });

  it("renders content when it has some", () => {
    expect(deriveAsyncRegionState(read({ hasContent: true }))).toEqual({
      status: "content",
      outage: false,
    });
  });
});

describe("a refresh that failed over content on screen", () => {
  it("keeps the content and raises the outage flag rather than reading as failed", () => {
    expect(deriveAsyncRegionState(read({ failed: true, hasContent: true }))).toEqual({
      status: "content",
      outage: true,
    });
  });
});

describe("the initial read", () => {
  it("does not derive the same state as a genuinely empty answer", () => {
    const initial = deriveAsyncRegionState(INITIAL_ASYNC_READ);
    const emptySuccess = deriveAsyncRegionState(read({}));
    expect(initial).not.toEqual(emptySuccess);
    expect(initial.status).not.toBe("empty");
  });
});

describe("the derivation", () => {
  it("holds nothing between calls", () => {
    const input = read({ hasContent: true, failed: true });
    const first = deriveAsyncRegionState(input);
    deriveAsyncRegionState(read({ reading: true }));
    expect(deriveAsyncRegionState(input)).toEqual(first);
  });
});

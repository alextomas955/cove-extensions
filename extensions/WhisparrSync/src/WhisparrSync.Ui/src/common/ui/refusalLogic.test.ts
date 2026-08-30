import { describe, expect, it } from "vitest";

import { CAP_UNAVAILABLE_ON_THIS_GENERATION } from "./copy";
import { describeRefusal, REFUSAL_KINDS } from "./refusalLogic";

describe("the four kinds are never collapsed", () => {
  it("holds exactly the four", () => {
    expect([...REFUSAL_KINDS].sort()).toEqual([
      "notConfigured",
      "nothingToDo",
      "unreachable",
      "versionCapability",
    ]);
  });

  it("gives no two kinds the same sentence", () => {
    const sentences = REFUSAL_KINDS.map((kind) => describeRefusal(kind).sentence).filter(
      (sentence) => sentence !== null,
    );
    expect(new Set(sentences).size).toBe(sentences.length);
  });

  it("gives every specified sentence something to say", () => {
    for (const kind of REFUSAL_KINDS) {
      const { sentence } = describeRefusal(kind);
      if (sentence !== null) {
        expect(sentence.trim(), kind).not.toBe("");
      }
    }
  });

  it("leaves the not-configured sentence to the surface that names its own setting", () => {
    expect(describeRefusal("notConfigured").sentence).toBeNull();
    expect(describeRefusal("notConfigured").affordances.namesASetting).toBe(true);
  });
});

describe("what each kind offers", () => {
  it("offers a retry only where asking again could answer differently", () => {
    const withRetry = REFUSAL_KINDS.filter((kind) => describeRefusal(kind).affordances.retry);
    expect(withRetry).toEqual(["unreachable"]);
  });

  it("names a setting only where a setting would fix it", () => {
    const withSetting = REFUSAL_KINDS.filter(
      (kind) => describeRefusal(kind).affordances.namesASetting,
    );
    expect(withSetting).toEqual(["notConfigured"]);
  });

  it("offers a version gap neither a retry nor a setting", () => {
    expect(describeRefusal("versionCapability").affordances).toEqual({
      retry: false,
      namesASetting: false,
    });
  });

  it("reads a version gap as the single-sourced sentence", () => {
    expect(describeRefusal("versionCapability").sentence).toBe(CAP_UNAVAILABLE_ON_THIS_GENERATION);
  });
});

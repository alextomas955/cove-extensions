import { describe, expect, it } from "vitest";

import { readEntityKind } from "./entityKindLogic";

describe("readEntityKind", () => {
  it("names the kind on each of the three host page types", () => {
    expect(readEntityKind("/studios/42")).toBe("studio");
    expect(readEntityKind("/performers/42")).toBe("performer");
    expect(readEntityKind("/tags/42")).toBe("tag");
  });

  it("reads the kind whatever follows the entity's own id", () => {
    expect(readEntityKind("/studios/42/")).toBe("studio");
    expect(readEntityKind("/studios/1234567")).toBe("studio");
  });

  it("answers null for a route it does not recognise", () => {
    expect(readEntityKind("/settings/whisparr-sync")).toBeNull();
    expect(readEntityKind("/videos/42")).toBeNull();
    expect(readEntityKind("/")).toBeNull();
  });

  it("answers null for a list page carrying the same word", () => {
    expect(readEntityKind("/studios")).toBeNull();
    expect(readEntityKind("/performers")).toBeNull();
  });
});

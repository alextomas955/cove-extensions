/**
 * Which kind of page the tab reads itself as being on.
 *
 * One component serves three page types and the host tells it nothing about which, so the address is
 * the only evidence. A route this does not recognise must answer null: guessing would read one
 * entity's catalogue on another entity's page, which is a wrong answer nothing on screen would
 * contradict.
 */
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

  /**
   * A segment naming a kind is not an entity page unless an id follows it. The host's own list
   * pages carry the same word and address no entity.
   */
  it("answers null for a list page carrying the same word", () => {
    expect(readEntityKind("/studios")).toBeNull();
    expect(readEntityKind("/performers")).toBeNull();
  });
});

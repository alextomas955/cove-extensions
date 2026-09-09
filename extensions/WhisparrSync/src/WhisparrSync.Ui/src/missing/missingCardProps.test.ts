/**
 * The contract the card offers the surface that mounts it, pinned against its own source.
 *
 * The tests run in a node environment, so no `.tsx` is rendered here and no prop shape can be read
 * off a rendered tree. A source pin is what keeps a prop rename or a swapped button variant from
 * passing every check in this bundle and reaching a browser.
 */
import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";

import { WAITING_FOR_WHISPARR } from "../common/ui/copy";

const source = readFileSync(path.join(import.meta.dirname, "MissingCard.tsx"), "utf8");

describe("the props the surface passes one card", () => {
  it("declares each of them once, by name", () => {
    for (const prop of [
      "card",
      "selected",
      "selecting",
      "onToggleSelect",
      "action",
      "onMonitor",
      "onSearch",
    ]) {
      expect(new RegExp(`^\\s{2}${prop}[,?:]`, "m").test(source), prop).toBe(true);
    }
  });

  it("names the scene each verb acts on from the card it was given", () => {
    expect(source).toContain("onMonitor(card.providerSceneId)");
    expect(source).toContain("onSearch(card.providerSceneId)");
  });
});

describe("the action row", () => {
  it("calls its two verbs what the product calls them", () => {
    expect(source).toContain('const MONITOR_LABEL = "Monitor"');
    expect(source).toContain('const SEARCH_LABEL = "Search"');
  });

  /**
   * Neither verb takes the accent fill. A page draws forty cards, so an accent-filled verb puts ten
   * solid blocks on screen at once and reads as the page's own instruction rather than as one card's
   * choice. A typecheck cannot see a variant, so the value is pinned here.
   */
  it("draws both verbs as the quiet variant", () => {
    const monitor = /name=\{MONITOR_LABEL\}\s*\n\s*variant="(\w+)"/.exec(source);
    const search = /name=\{SEARCH_LABEL\}\s*\n\s*variant="(\w+)"/.exec(source);

    expect(monitor?.[1]).toBe("ghost");
    expect(search?.[1]).toBe("ghost");
  });

  it("dims a verb only while its own request is in the air, and always says why", () => {
    for (const verb of ["monitor", "search"]) {
      expect(source).toContain(
        `reason={action.inFlight === "${verb}" ? WAITING_FOR_WHISPARR : null}`,
      );
    }

    expect(WAITING_FOR_WHISPARR).not.toBe("");

    // A capability the connected generation cannot honour is an absent control. The only disabled
    // path on this card is the transient one above.
    expect(source).not.toMatch(/disabled(=|\s*[,}])/);
  });
});

describe("what the card never does", () => {
  it("renders no provider string as markup", () => {
    expect(source).not.toContain("dangerouslySetInnerHTML");
  });

  it("paints nothing red, and reaches the status vocabulary rather than the pill", () => {
    expect(source).not.toMatch(/\b(text|bg|border|ring)-red\b/);
    expect(source).not.toContain("StatusPill");
  });

  it("carries no card-level link, because a provider scene has no page in Cove", () => {
    expect(source).not.toMatch(/<a[\s>]/);
    expect(source).not.toContain("RouteCardLinkOverlay");
  });

  it("uses the focus ring the host stylesheet actually emits", () => {
    expect(source).toContain("focus:ring-2 focus:ring-accent");
    expect(source).not.toContain("focus-visible:ring");
  });
});

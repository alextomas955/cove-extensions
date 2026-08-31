/**
 * The banner's pure rules: its cause vocabulary, whether there is anything to say, the order the
 * roots read in, and that the line no root was reported for still reads as a sentence.
 *
 * The cause spellings are transcribed by hand from the server's own enum. An expectation computed
 * from the generated module agrees with it forever and reports nothing.
 */
import { describe, expect, it } from "vitest";

import type { ImportBannerRootLine, ImportBannerView, ImportRefusalCause } from "../wire/api";
import {
  bannerLines,
  describeCause,
  hasAnythingToSay,
  headingFor,
  IMPORT_REFUSAL_CAUSES,
  NEWEST_PATHS_SHOWN,
  NO_REPORTED_ROOT,
  pathsShownFor,
} from "./importBannerLogic";

/** The three spellings the server emits, written out rather than read off the wire module. */
const CAUSES: readonly ImportRefusalCause[] = [
  "notFoundUnderAnyRoot",
  "ambiguousCandidates",
  "unreadable",
];

function lineFor(root: string, count: number, paths: number): ImportBannerRootLine {
  return {
    root,
    countSinceLastSuccess: count,
    newestPaths: Array.from({ length: paths }, (_, index) => ({
      path: `${root}/${String(index)}.mp4`,
      cause: "notFoundUnderAnyRoot" as const,
    })),
  };
}

function viewOf(...roots: ImportBannerRootLine[]): ImportBannerView {
  return { roots };
}

describe("the cause vocabulary", () => {
  it("has exactly the three causes the server emits", () => {
    expect(IMPORT_REFUSAL_CAUSES).toEqual(CAUSES);
  });

  it("gives every cause a sentence of its own", () => {
    const sentences = CAUSES.map((cause) => describeCause(cause));

    for (const [index, sentence] of sentences.entries()) {
      expect(sentence, CAUSES[index]).not.toBe("");
    }
    expect(new Set(sentences).size, "two causes read identically").toBe(CAUSES.length);
  });
});

describe("whether there is anything to say", () => {
  it("says nothing before the read answers", () => {
    expect(hasAnythingToSay(null)).toBe(false);
    expect(bannerLines(null)).toEqual([]);
  });

  it("says nothing for an answer with no roots", () => {
    expect(hasAnythingToSay(viewOf())).toBe(false);
  });

  it("says something for one root with one refusal", () => {
    expect(hasAnythingToSay(viewOf(lineFor("/whisparr-media", 1, 1)))).toBe(true);
  });
});

describe("the order the roots read in", () => {
  it("puts named roots in their own order", () => {
    const lines = bannerLines(
      viewOf(lineFor("/z-root", 1, 1), lineFor("/a-root", 1, 1), lineFor("/m-root", 1, 1)),
    );

    expect(lines.map((line) => line.root)).toEqual(["/a-root", "/m-root", "/z-root"]);
  });

  it("puts the line no root was reported for last, and keeps it", () => {
    const lines = bannerLines(
      viewOf(lineFor(NO_REPORTED_ROOT, 2, 1), lineFor("/z-root", 1, 1), lineFor("/a-root", 1, 1)),
    );

    expect(lines.map((line) => line.root)).toEqual(["/a-root", "/z-root", NO_REPORTED_ROOT]);
  });
});

describe("how a root's line is headed", () => {
  it("names the root and the stored count", () => {
    const heading = headingFor(lineFor("/whisparr-media", 12, 3));

    expect(heading).toContain("/whisparr-media");
    expect(heading).toContain("12");
  });

  it("reads as a sentence for the line no root was reported for", () => {
    const heading = headingFor(lineFor(NO_REPORTED_ROOT, 4, 1));

    expect(heading.trim(), "the blank key reached the reader as itself").not.toBe("");
    expect(heading).toContain("4");
    // The blank key would otherwise read as a heading with a hole in it.
    expect(heading).not.toContain("  ");
  });

  it("agrees with the count for one file", () => {
    expect(headingFor(lineFor("/whisparr-media", 1, 1))).toContain("1 file ");
    expect(headingFor(lineFor(NO_REPORTED_ROOT, 1, 1))).toContain("1 file ");
  });
});

describe("how many paths a line lists", () => {
  it("lists them all while there are no more than the bound", () => {
    expect(pathsShownFor(lineFor("/whisparr-media", 2, 2))).toHaveLength(2);
  });

  it("lists the bound and no more when handed a longer list", () => {
    const shown = pathsShownFor(lineFor("/whisparr-media", 9, 9));

    expect(shown).toHaveLength(NEWEST_PATHS_SHOWN);
    expect(shown.map((path) => path.path)).toEqual([
      "/whisparr-media/0.mp4",
      "/whisparr-media/1.mp4",
      "/whisparr-media/2.mp4",
    ]);
  });

  it("keeps the bound at the three the stored aggregate holds", () => {
    expect(NEWEST_PATHS_SHOWN).toBe(3);
  });
});

import { describe, expect, it } from "vitest";

import type {
  FolderAgreementRefusal,
  FolderAgreementRootLine,
  FolderAgreementView,
  FolderMappingSaveResult,
} from "../wire/api";
import {
  FOLDER_AGREEMENT_SETTLED,
  FOLDER_INSTANCE_CANNOT_BE_ASKED,
  FOLDER_MORE_THAN_ONE_RESOLVED,
  FOLDER_NO_FILE_TO_PROBE_WITH,
  FOLDER_NOTHING_RESOLVED,
  FOLDER_PROBE_COULD_NOT_BE_READ,
  FOLDER_SAVE_DID_NOT_REACH,
  FOLDER_SAVE_NOT_A_LIBRARY_ROOT,
  FOLDER_SAVE_NOT_CONFIGURED,
  FOLDER_SAVE_REMOVED,
  FOLDER_SAVE_STORED,
} from "../common/ui/copy";
import {
  agreementLines,
  asksForAPath,
  describeFolderRefusal,
  FOLDER_AGREEMENT_REFUSALS,
  hasAnythingToShow,
  mappingSentenceFor,
  saveAnswerSentence,
  saveSettled,
  sentenceFor,
  withdrawsOnly,
  type FolderSaveAnswer,
} from "./folderAgreementLogic";

function lineFor(
  root: string,
  refusal: FolderAgreementRefusal,
  pathsTried: string[] = [],
  mapping: string | null = null,
): FolderAgreementRootLine {
  return { root, refusal, pathsTried, mapping };
}

function settledLineFor(root: string, mapping: string): FolderAgreementRootLine {
  return { root, refusal: null, pathsTried: [], mapping };
}

function viewOf(...roots: FolderAgreementRootLine[]): FolderAgreementView {
  return { roots };
}

function answered(result: FolderMappingSaveResult): FolderSaveAnswer {
  return { kind: "answered", result };
}

describe("a page with every folder settled has nothing to show", () => {
  it("says there is nothing to ask about for an answer with no folders", () => {
    expect(hasAnythingToShow(viewOf())).toBe(false);
    expect(agreementLines(viewOf())).toEqual([]);
  });

  it("says the same before the read has answered at all", () => {
    expect(hasAnythingToShow(null)).toBe(false);
    expect(agreementLines(null)).toEqual([]);
  });

  it("has something to ask about as soon as one folder is unsettled", () => {
    expect(hasAnythingToShow(viewOf(lineFor("/media", "nothingResolved")))).toBe(true);
  });
});

describe("each folder says what happened to it", () => {
  it("names the folder and the path that was tried where nothing resolved", () => {
    const sentence = sentenceFor(
      lineFor("/media", "nothingResolved", ["/data/media/scene/clip.mp4"]),
    );

    expect(sentence).toContain("/media");
    expect(sentence).toContain(FOLDER_NOTHING_RESOLVED);
    expect(sentence).toContain("/data/media/scene/clip.mp4");
  });

  it("says the places cannot be told apart, and that a stated path settles it", () => {
    const sentence = sentenceFor(
      lineFor("/media", "moreThanOneResolved", ["/data/media", "/mnt/media"]),
    );

    expect(sentence).toContain(FOLDER_MORE_THAN_ONE_RESOLVED);
    expect(sentence).toContain("/data/media");
    expect(sentence).toContain("/mnt/media");
  });

  it("states a folder with no file to ask about, and asks for nothing", () => {
    expect(sentenceFor(lineFor("/media", "noFileToProbeWith"))).toContain(
      FOLDER_NO_FILE_TO_PROBE_WITH,
    );
    expect(asksForAPath(lineFor("/media", "noFileToProbeWith"))).toBe(false);
  });

  it("separates an answer that could not be read from an answer of no", () => {
    expect(describeFolderRefusal("probeCouldNotBeRead")).toBe(FOLDER_PROBE_COULD_NOT_BE_READ);
    expect(describeFolderRefusal("instanceCannotBeAsked")).toBe(FOLDER_INSTANCE_CANNOT_BE_ASKED);
    expect(describeFolderRefusal("probeCouldNotBeRead")).not.toBe(
      describeFolderRefusal("nothingResolved"),
    );
  });

  it("gives every refusal a sentence of its own", () => {
    const sentences = FOLDER_AGREEMENT_REFUSALS.map(describeFolderRefusal);

    for (const sentence of sentences) {
      expect(sentence.length).toBeGreaterThan(0);
    }
    expect(new Set(sentences).size).toBe(FOLDER_AGREEMENT_REFUSALS.length);
  });

  it("asks for a path for every refusal a stated one could settle", () => {
    const asking = FOLDER_AGREEMENT_REFUSALS.filter((refusal) =>
      asksForAPath(lineFor("/media", refusal)),
    );

    expect([...asking].sort()).toEqual(
      [
        "folderUnderNoLibraryRoot",
        "instanceCannotBeAsked",
        "instanceDeclaresNoRoot",
        "moreThanOneResolved",
        "nothingResolved",
        "probeCouldNotBeRead",
      ].sort(),
    );
  });
});

describe("a folder whose stated path is working", () => {
  it("names the folder and says nothing about it is outstanding", () => {
    const sentence = sentenceFor(settledLineFor("/media", "/data/media"));

    expect(sentence).toContain("/media");
    expect(sentence).toContain(FOLDER_AGREEMENT_SETTLED);
  });

  it("names no path the instance was asked about", () => {
    expect(sentenceFor(settledLineFor("/media", "/data/media"))).not.toContain("asked Whisparr");
  });

  it("names the path in force beneath the folder", () => {
    const sentence = mappingSentenceFor(settledLineFor("/media", "/data/media"));

    expect(sentence ?? "").toContain("/data/media");
  });

  it("asks for a path, so the stated one can be withdrawn", () => {
    expect(asksForAPath(settledLineFor("/media", "/data/media"))).toBe(true);
    expect(withdrawsOnly(settledLineFor("/media", "/data/media"))).toBe(false);
  });

  it("is something to show on a page holding nothing else", () => {
    expect(hasAnythingToShow(viewOf(settledLineFor("/media", "/data/media")))).toBe(true);
  });
});

describe("a folder carrying a stated path and a reason at once", () => {
  it("offers only the withdrawal where no path typed could be stored", () => {
    const line = lineFor("/media", "noFileToProbeWith", [], "/data/media");

    expect(withdrawsOnly(line)).toBe(true);
    expect(asksForAPath(line)).toBe(false);
  });

  it("asks for a path where a typed one could settle the reason", () => {
    const line = lineFor("/media", "nothingResolved", ["/data/media"], "/mnt/media");

    expect(withdrawsOnly(line)).toBe(false);
    expect(asksForAPath(line)).toBe(true);
  });

  it("offers neither where that reason carries no stated path", () => {
    const line = lineFor("/media", "noFileToProbeWith");

    expect(asksForAPath(line)).toBe(false);
    expect(withdrawsOnly(line)).toBe(false);
  });
});

describe("a folder with a path already stated shows it", () => {
  it("names the path in force", () => {
    const sentence = mappingSentenceFor(
      lineFor("/media", "nothingResolved", ["/data/media"], "/data/media"),
    );

    expect(sentence).not.toBeNull();
    expect(sentence ?? "").toContain("/data/media");
  });

  it("says nothing where no path is stated", () => {
    expect(mappingSentenceFor(lineFor("/media", "nothingResolved", ["/data/media"]))).toBeNull();
  });
});

describe("what one save came to", () => {
  it("reads as settled where the path was stored", () => {
    const answer = answered({ outcome: "stored", refusal: null, tried: ["/data/media"] });

    expect(saveAnswerSentence(answer)).toBe(FOLDER_SAVE_STORED);
    expect(saveSettled(answer)).toBe(true);
  });

  it("reads as settled where a blank path removed the stored one", () => {
    const answer = answered({ outcome: "removed", refusal: null, tried: [] });

    expect(saveAnswerSentence(answer)).toBe(FOLDER_SAVE_REMOVED);
    expect(saveSettled(answer)).toBe(true);
  });

  it("carries the refusal and the path tried where the path did not resolve", () => {
    const answer = answered({
      outcome: "refused",
      refusal: "nothingResolved",
      tried: ["/wrong/media/scene/clip.mp4"],
    });
    const sentence = saveAnswerSentence(answer);

    expect(sentence).toContain(FOLDER_NOTHING_RESOLVED);
    expect(sentence).toContain("/wrong/media/scene/clip.mp4");
    expect(saveSettled(answer)).toBe(false);
  });

  it("has its own sentence for a path that is none of Cove's folders", () => {
    const answer = answered({ outcome: "notALibraryRoot", refusal: null, tried: [] });

    expect(saveAnswerSentence(answer)).toBe(FOLDER_SAVE_NOT_A_LIBRARY_ROOT);
    expect(saveSettled(answer)).toBe(false);
  });

  it("has its own sentence where no instance was connected to ask", () => {
    const answer = answered({ outcome: "notConfigured", refusal: null, tried: [] });

    expect(saveAnswerSentence(answer)).toBe(FOLDER_SAVE_NOT_CONFIGURED);
    expect(saveSettled(answer)).toBe(false);
  });

  it("has its own sentence where the save never reached Cove", () => {
    const answer: FolderSaveAnswer = { kind: "didNotReach" };

    expect(saveAnswerSentence(answer)).toBe(FOLDER_SAVE_DID_NOT_REACH);
    expect(saveSettled(answer)).toBe(false);
  });
});

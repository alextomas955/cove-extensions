/**
 * That the page's four reasons stay four, and that neither of the two settled before anything left
 * Cove says a word about reaching Whisparr.
 *
 * The expectations are transcribed from the sentences rather than computed from the table, because a
 * check derived from the module it checks agrees with itself however the sentences are wired.
 */
import { expect, test } from "vitest";

import {
  NO_WHISPARR_CONNECTED,
  THE_STATUS_READ_DID_NOT_COMPLETE,
  WHISPARR_KEEPS_NO_RECORD_OF_THESE,
  WHISPARR_STATUS_COULD_NOT_BE_READ,
} from "../common/ui/copy";
import { libraryRefusalSentence, LIBRARY_PAGE_REFUSALS } from "./libraryRefusalLogic";

/** The reasons for which nothing left Cove, so no claim about Whisparr can be made either way. */
const NOTHING_WAS_ASKED = ["noInstanceConnected", "whisparrCannotAnswerForThisKind"] as const;

test("an answered page states nothing", () => {
  expect(libraryRefusalSentence("none")).toBeNull();
});

test("each reason states its own sentence", () => {
  expect(libraryRefusalSentence("noInstanceConnected")).toBe(NO_WHISPARR_CONNECTED);
  expect(libraryRefusalSentence("whisparrCannotAnswerForThisKind")).toBe(
    WHISPARR_KEEPS_NO_RECORD_OF_THESE,
  );
  expect(libraryRefusalSentence("instanceUnreachable")).toBe(WHISPARR_STATUS_COULD_NOT_BE_READ);
  expect(libraryRefusalSentence("statusCouldNotBeRead")).toBe(THE_STATUS_READ_DID_NOT_COMPLETE);
});

test("no two reasons share a sentence", () => {
  const stated = LIBRARY_PAGE_REFUSALS.map(libraryRefusalSentence).filter(
    (sentence) => sentence !== null,
  );

  expect(stated).toHaveLength(LIBRARY_PAGE_REFUSALS.length - 1);
  expect(new Set(stated).size, "two reasons collapsed onto one sentence").toBe(stated.length);
});

test("a reason for which nothing was asked claims nothing about reaching Whisparr", () => {
  for (const refusal of NOTHING_WAS_ASKED) {
    const stated = libraryRefusalSentence(refusal) ?? "";

    expect(stated, refusal).not.toBe("");
    expect(stated.toLowerCase(), `${refusal} claims Whisparr could not be reached`).not.toContain(
      "could not reach",
    );
  }
});

test("every reason a page can carry has a sentence, or is the answered one", () => {
  for (const refusal of LIBRARY_PAGE_REFUSALS) {
    const stated = libraryRefusalSentence(refusal);
    expect(refusal === "none" || (stated !== null && stated.trim() !== ""), refusal).toBe(true);
  }
});

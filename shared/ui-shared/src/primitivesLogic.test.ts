/** Behavior contract for the pure primitive logic. */
import { test } from "vitest";
import assert from "node:assert/strict";

import {
  isRegexValid,
  isAbsolutePathShape,
  extensionShapeAdvisory,
  nextActiveIndex,
  numberInputValue,
  suggestionOptions,
} from "./primitivesLogic";

test("a well-formed pattern is valid", () => {
  assert.deepEqual(isRegexValid("^C:/in/.*$"), { valid: true });
});

test("an empty pattern is valid (an empty rule pattern is a no-op)", () => {
  assert.deepEqual(isRegexValid(""), { valid: true });
});

test("a malformed pattern is invalid and carries a non-empty message", () => {
  const result = isRegexValid("(unclosed");
  assert.equal(result.valid, false);
  assert.ok(typeof result.message === "string" && result.message.length > 0);
});

test.each([
  ["a Windows drive-letter path looks absolute", "C:\\Users\\x", true],
  ["a drive-letter path in the other slash style looks absolute", "D:/media", true],
  ["a POSIX leading-slash path looks absolute", "/mnt/media", true],
  ["a UNC path looks absolute", "\\\\server\\share", true],
  ["a relative path does not look absolute", "relative/path", false],
  ["a bare name does not look absolute", "media", false],
  ["a blank value is never flagged as implausible", "", true],
  ["a whitespace-only value is never flagged as implausible", "   ", true],
] as const)("%s", (_name, value, absolute) => {
  assert.equal(isAbsolutePathShape(value), absolute);
});

const SHAPE_ADVISORY = "Extensions are letters and numbers only, like srt or nfo.";
const PRIMARY_MEDIA_ADVISORY = "This looks like a primary media extension, not a sidecar.";

test.each([
  ["a bare lowercase alphanumeric extension has no advisory", "srt", null],
  ["another bare extension has no advisory either", "nfo", null],
  ["an extension carrying a space is rejected with the shape message", "sr t", SHAPE_ADVISORY],
  ["an extension carrying punctuation is rejected the same way", "srt!!", SHAPE_ADVISORY],
  ["a primary video extension is called out as one", "mp4", PRIMARY_MEDIA_ADVISORY],
  ["a primary image extension is called out as one", "jpg", PRIMARY_MEDIA_ADVISORY],
  ["an empty extension value has no advisory", "", null],
] as const)("%s", (_name, value, advisory) => {
  assert.equal(extensionShapeAdvisory(value), advisory);
});

const TOKENS = ["title", "studio", "parentStudio", "studioCode", "date", "year"];

test("with nothing picked and a blank query, every suggestion is offered in the set's order", () => {
  assert.deepEqual(suggestionOptions(TOKENS, [], ""), TOKENS);
  assert.deepEqual(suggestionOptions(TOKENS, [], "   "), TOKENS);
});

test("a suggestion already picked is not offered again", () => {
  assert.deepEqual(suggestionOptions(TOKENS, ["studio", "year"], ""), [
    "title",
    "parentStudio",
    "studioCode",
    "date",
  ]);
});

test("a folding key treats a pick in another case as the same pick", () => {
  const fold = (value: string) => value.toLowerCase();
  assert.deepEqual(suggestionOptions(TOKENS, ["STUDIO", "Year"], "", fold), [
    "title",
    "parentStudio",
    "studioCode",
    "date",
  ]);
  // The offer list is the original spellings, never the folded ones: the fold is an identity, not a
  // value to hand back.
  assert.deepEqual(suggestionOptions(["Title", "Studio"], [], "", fold), ["Title", "Studio"]);
});

test("without a key a pick in another case is a different pick", () => {
  assert.deepEqual(suggestionOptions(TOKENS, ["STUDIO"], "studio"), [
    "studio",
    "parentStudio",
    "studioCode",
  ]);
});

test("the query is trimmed, filters case-insensitively and keeps the suggestion set's order", () => {
  assert.deepEqual(suggestionOptions(TOKENS, [], "stud"), ["studio", "parentStudio", "studioCode"]);
  assert.deepEqual(suggestionOptions(TOKENS, [], "STUD"), ["studio", "parentStudio", "studioCode"]);
  assert.deepEqual(suggestionOptions(TOKENS, [], "  year  "), ["year"]);
});

test("a query matching nothing offers nothing, so the caller can hide the list", () => {
  assert.deepEqual(suggestionOptions(TOKENS, [], "zzz"), []);
});

test.each([
  ["an empty list has no active index going forward", -1, 0, 1, -1],
  ["an empty list has no active index going back", -1, 0, -1, -1],
  ["an empty list has none from an index it never held", 2, 0, 1, -1],
  ["from no selection, forward takes the first option", -1, 4, 1, 0],
  ["from no selection, back takes the last option", -1, 4, -1, 3],
  ["the active index wraps forward off the end", 3, 4, 1, 0],
  ["the active index wraps back off the start", 0, 4, -1, 3],
  ["an index past the end of a shrunken list counts as no selection going forward", 9, 4, 1, 0],
  ["an index past the end of a shrunken list counts as no selection going back", 9, 4, -1, 3],
] as const)("%s", (_name, active, count, step, next) => {
  assert.equal(nextActiveIndex(active, count, step), next);
});

test.each([
  ["a number field shows the value it holds", 7, undefined, 7],
  ["a number field shows a zero it holds", 0, undefined, 0],
  ["a zero that does not mean unset is shown", 0, false, 0],
  ["a zero that means unset shows nothing, so the placeholder reads", 0, true, ""],
  ["a value whose zero would mean unset is still shown", 7, true, 7],
  ["a field shows nothing for a value that is not a number", Number.NaN, undefined, ""],
  ["a field shows nothing for one that is not a number either way", Number.NaN, true, ""],
] as const)("%s", (_name, value, blankWhenZero, shown) => {
  assert.equal(numberInputValue(value, blankWhenZero), shown);
});

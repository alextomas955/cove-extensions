/** Behavior contract for the pure primitive logic. */
import { test } from "vitest";
import assert from "node:assert/strict";

import {
  filterByText,
  isRegexValid,
  isAbsolutePathShape,
  extensionShapeAdvisory,
  nextActiveIndex,
  numberInputValue,
  suggestionOptions,
} from "./primitivesLogic";

const items = [{ name: "Alpha" }, { name: "beta" }, { name: "Gamma" }, { name: "alphabet" }];
const byName = (item: { name: string }) => item.name;

test("a blank query returns the full list in original order", () => {
  assert.deepEqual(filterByText("", items, byName), items);
  assert.deepEqual(filterByText("   ", items, byName), items);
});

test("the filter matches case-insensitively as a substring", () => {
  const result = filterByText("alph", items, byName);
  assert.deepEqual(result.map(byName), ["Alpha", "alphabet"]);
});

test("the query is trimmed before comparing", () => {
  assert.deepEqual(filterByText("  gamma  ", items, byName).map(byName), ["Gamma"]);
});

test("a query that matches nothing returns an empty list", () => {
  assert.deepEqual(filterByText("zzz", items, byName), []);
});

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

test("a Windows drive-letter path looks absolute, both slash styles", () => {
  assert.equal(isAbsolutePathShape("C:\\Users\\x"), true);
  assert.equal(isAbsolutePathShape("D:/media"), true);
});

test("a POSIX or UNC leading-slash path looks absolute", () => {
  assert.equal(isAbsolutePathShape("/mnt/media"), true);
  assert.equal(isAbsolutePathShape("\\\\server\\share"), true);
});

test("a relative path does not look absolute", () => {
  assert.equal(isAbsolutePathShape("relative/path"), false);
  assert.equal(isAbsolutePathShape("media"), false);
});

test("a blank or whitespace-only value is never flagged as implausible", () => {
  assert.equal(isAbsolutePathShape(""), true);
  assert.equal(isAbsolutePathShape("   "), true);
});

test("a bare lowercase alphanumeric extension has no advisory", () => {
  assert.equal(extensionShapeAdvisory("srt"), null);
  assert.equal(extensionShapeAdvisory("nfo"), null);
});

test("a shape-invalid extension is rejected with the shape message", () => {
  assert.equal(
    extensionShapeAdvisory("sr t"),
    "Extensions are letters and numbers only, like srt or nfo.",
  );
  assert.equal(
    extensionShapeAdvisory("srt!!"),
    "Extensions are letters and numbers only, like srt or nfo.",
  );
});

test("a primary media extension gets the duplicate-of-primary-media advisory", () => {
  assert.equal(
    extensionShapeAdvisory("mp4"),
    "This looks like a primary media extension, not a sidecar.",
  );
  assert.equal(
    extensionShapeAdvisory("jpg"),
    "This looks like a primary media extension, not a sidecar.",
  );
});

test("an empty extension value has no advisory", () => {
  assert.equal(extensionShapeAdvisory(""), null);
});

const TOKENS = ["title", "studio", "parentStudio", "studioCode", "date", "year"];

test("with nothing picked and no query, every suggestion is offered in the set's order", () => {
  assert.deepEqual(suggestionOptions(TOKENS, [], ""), TOKENS);
});

test("a suggestion already picked is not offered again", () => {
  assert.deepEqual(suggestionOptions(TOKENS, ["studio", "year"], ""), [
    "title",
    "parentStudio",
    "studioCode",
    "date",
  ]);
});

test("the query filters case-insensitively and keeps the suggestion set's order", () => {
  assert.deepEqual(suggestionOptions(TOKENS, [], "stud"), ["studio", "parentStudio", "studioCode"]);
  assert.deepEqual(suggestionOptions(TOKENS, [], "STUD"), ["studio", "parentStudio", "studioCode"]);
});

test("a query matching nothing offers nothing, so the caller can hide the list", () => {
  assert.deepEqual(suggestionOptions(TOKENS, [], "zzz"), []);
});

test("an empty list has no active index in either direction", () => {
  assert.equal(nextActiveIndex(-1, 0, 1), -1);
  assert.equal(nextActiveIndex(-1, 0, -1), -1);
  assert.equal(nextActiveIndex(2, 0, 1), -1);
});

test("from no selection, forward takes the first option and back takes the last", () => {
  assert.equal(nextActiveIndex(-1, 4, 1), 0);
  assert.equal(nextActiveIndex(-1, 4, -1), 3);
});

test("the active index wraps at both ends", () => {
  assert.equal(nextActiveIndex(3, 4, 1), 0);
  assert.equal(nextActiveIndex(0, 4, -1), 3);
});

test("an index past the end of a shrunken list counts as no selection", () => {
  assert.equal(nextActiveIndex(9, 4, 1), 0);
  assert.equal(nextActiveIndex(9, 4, -1), 3);
});

test("a number field shows every value it holds, zero included", () => {
  assert.equal(numberInputValue(0), 0);
  assert.equal(numberInputValue(0, false), 0);
  assert.equal(numberInputValue(7), 7);
});

test("a number field whose zero means unset shows nothing for it, so the placeholder reads", () => {
  assert.equal(numberInputValue(0, true), "");
  assert.equal(numberInputValue(7, true), 7);
});

test("a number field shows nothing for a value that is not a number", () => {
  assert.equal(numberInputValue(Number.NaN), "");
  assert.equal(numberInputValue(Number.NaN, true), "");
});

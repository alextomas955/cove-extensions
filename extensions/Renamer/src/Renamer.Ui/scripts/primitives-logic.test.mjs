/**
 * Behavior contract for the pure primitive logic. The runner compiles primitivesLogic.ts and passes
 * the compiled module path in PRIMITIVES_LOGIC_MODULE; importing the exact compiled artifact keeps the
 * test honest about what ships.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.PRIMITIVES_LOGIC_MODULE);
const { filterByText, isRegexValid, isAbsolutePathShape, extensionShapeAdvisory } = mod;

const items = [{ name: "Alpha" }, { name: "beta" }, { name: "Gamma" }, { name: "alphabet" }];
const byName = (item) => item.name;

test("a blank query returns the full list in original order", () => {
  assert.deepEqual(filterByText("", items, byName), items);
  assert.deepEqual(filterByText("   ", items, byName), items);
});

test("the filter matches case-insensitively as a substring", () => {
  const result = filterByText("alph", items, byName);
  assert.deepEqual(
    result.map(byName),
    ["Alpha", "alphabet"],
  );
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

/**
 * Behavior contract for the searchable-select filter/rank. The runner compiles searchableSelectLogic.ts
 * and passes the compiled module URL via SEARCHABLE_SELECT_LOGIC_MODULE.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.SEARCHABLE_SELECT_LOGIC_MODULE);
const { filterOptions, nextActiveIndex } = mod;

const opts = [
  { value: "", label: "All performers" },
  { value: "Jane Doe", label: "Jane Doe" },
  { value: "Janet Smith", label: "Janet Smith" },
  { value: "Mary Jane", label: "Mary Jane" },
  { value: "Bob Jones", label: "Bob Jones" },
];

test("filterOptions: an empty query returns every option in caller order", () => {
  assert.deepEqual(
    filterOptions(opts, "").map((o) => o.value),
    opts.map((o) => o.value),
  );
  assert.deepEqual(
    filterOptions(opts, "   ").map((o) => o.value),
    opts.map((o) => o.value),
  );
});

test("filterOptions: a query narrows to substring matches only (case-insensitive)", () => {
  const labels = filterOptions(opts, "jane").map((o) => o.label);
  // "All performers" and "Bob Jones" drop out; the three Jane* labels remain.
  assert.deepEqual([...labels].sort(), ["Jane Doe", "Janet Smith", "Mary Jane"]);
});

test("filterOptions: ranks prefix and word-start above a mid-word substring", () => {
  // "jan": prefix on "Jane Doe"/"Janet Smith", word-start on "Mary Jane"; all outrank none here.
  const ranked = filterOptions(opts, "jan").map((o) => o.label);
  assert.equal(ranked[0], "Jane Doe"); // shortest prefix match leads
  assert.equal(ranked[1], "Janet Smith");
  assert.equal(ranked[2], "Mary Jane"); // word-start, ranked after the two prefixes
});

test("filterOptions: no matches yields an empty list", () => {
  assert.deepEqual(filterOptions(opts, "zzz"), []);
});

test("nextActiveIndex: wraps at both ends and handles an empty list", () => {
  assert.equal(nextActiveIndex(0, 3, 1), 1);
  assert.equal(nextActiveIndex(2, 3, 1), 0); // wrap forward
  assert.equal(nextActiveIndex(0, 3, -1), 2); // wrap backward
  assert.equal(nextActiveIndex(-1, 3, 1), 0); // from no-highlight, down → first
  assert.equal(nextActiveIndex(-1, 3, -1), 2); // from no-highlight, up → last
  assert.equal(nextActiveIndex(0, 0, 1), -1); // empty list → no highlight
});

/** Behavior contract for the pure studio-map coercion. */
import { test } from "vitest";
import assert from "node:assert/strict";

import { toStringKeyed, fromStringKeyed } from "./studioMapLogic";

test("a number-keyed map becomes a string-keyed map preserving values", () => {
  assert.deepEqual(toStringKeyed({ 3: "/a", 12: "/b" }), { 3: "/a", 12: "/b" });
});

test("a round-trip through string keys restores number keys identically", () => {
  const original = { 3: "/a", 12: "/b" };
  const back = fromStringKeyed(toStringKeyed(original));
  assert.deepEqual(back, original);
});

test("every back-converted key is an integer (value-equal with the backend's number keys)", () => {
  // JS object keys are always strings at the JS level, so a `typeof` check would be tautological;
  // the real invariant is that each key round-trips to an integer (no NaN/float survives).
  const back = fromStringKeyed({ 7: "/x", 42: "/y" });
  assert.ok(Object.keys(back).every((k) => Number.isInteger(Number(k))));
  assert.equal(back[7], "/x");
});

test("a non-integer key is dropped rather than producing a NaN key", () => {
  const back = fromStringKeyed({ x: "/a", 1.5: "/b", 9: "/c" });
  assert.deepEqual(back, { 9: "/c" });
});

test("a non-string value is dropped on back-conversion", () => {
  // The number value is off-contract on purpose and the cast is what says so: the guard under test
  // exists for a stored blob that a previous version or a hand edit left holding a non-string, which
  // the declared parameter type cannot describe. Casting the input keeps the guard exercised; widening
  // the signature would retire it.
  const back = fromStringKeyed({ 4: 12, 5: "/ok" } as unknown as Record<string, string>);
  assert.deepEqual(back, { 5: "/ok" });
});

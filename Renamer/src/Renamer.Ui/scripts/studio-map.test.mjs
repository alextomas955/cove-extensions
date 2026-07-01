/**
 * Behavior contract for the pure studio-map coercion. The runner compiles studioMapLogic.ts and passes the
 * compiled module path in STUDIO_MAP_MODULE; importing the exact compiled artifact keeps the test
 * honest about what ships.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.STUDIO_MAP_MODULE);
const { toStringKeyed, fromStringKeyed } = mod;

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
  const back = fromStringKeyed({ "7": "/x", "42": "/y" });
  assert.ok(Object.keys(back).every((k) => Number.isInteger(Number(k))));
  assert.equal(back[7], "/x");
});

test("a non-integer key is dropped rather than producing a NaN key", () => {
  const back = fromStringKeyed({ x: "/a", "1.5": "/b", "9": "/c" });
  assert.deepEqual(back, { 9: "/c" });
});

test("a non-string value is dropped on back-conversion", () => {
  const back = fromStringKeyed({ 4: 12, 5: "/ok" });
  assert.deepEqual(back, { 5: "/ok" });
});

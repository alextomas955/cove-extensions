/**
 * Behavior contract for the pure guided-setup logic. The runner (check-guided-setup-logic.mjs) compiles
 * guidedSetupLogic.ts and passes the compiled module URL via GUIDED_SETUP_LOGIC_MODULE.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.GUIDED_SETUP_LOGIC_MODULE);
const {
  identityHealthFromServer,
  hasUnidentified,
  guidedSetupSummary,
  GUIDED_SETUP_HEADING,
  NO_IDENTITY_PROBLEMS,
} = mod;

test("identityHealthFromServer: reads a valid payload; malformed input is a healthy zero", () => {
  const h = identityHealthFromServer({ totalScenes: 42, unidentifiedScenes: 7 });
  assert.equal(h.totalScenes, 42);
  assert.equal(h.unidentifiedScenes, 7);

  assert.deepEqual(identityHealthFromServer(null), NO_IDENTITY_PROBLEMS);
  assert.deepEqual(identityHealthFromServer("nope"), NO_IDENTITY_PROBLEMS);
  assert.deepEqual(identityHealthFromServer({}), NO_IDENTITY_PROBLEMS);
  // Negatives / non-finite / fractional inputs clamp to a floored non-negative integer.
  assert.equal(identityHealthFromServer({ unidentifiedScenes: -3 }).unidentifiedScenes, 0);
  assert.equal(identityHealthFromServer({ unidentifiedScenes: 2.9 }).unidentifiedScenes, 2);
  assert.equal(identityHealthFromServer({ unidentifiedScenes: Infinity }).unidentifiedScenes, 0);
});

test("hasUnidentified: true only when at least one scene lacks a provider id", () => {
  assert.equal(hasUnidentified({ totalScenes: 10, unidentifiedScenes: 1 }), true);
  assert.equal(hasUnidentified(NO_IDENTITY_PROBLEMS), false);
  assert.equal(hasUnidentified({ totalScenes: 10, unidentifiedScenes: 0 }), false);
});

test("guidedSetupSummary: null at the zero-state (banner renders nothing); provider-named otherwise", () => {
  // Zero-state → no banner. This is the quiet-by-default contract.
  assert.equal(guidedSetupSummary(NO_IDENTITY_PROBLEMS, "StashDB"), null);
  assert.equal(guidedSetupSummary({ totalScenes: 5, unidentifiedScenes: 0 }, "StashDB"), null);

  const v3 = guidedSetupSummary({ totalScenes: 100, unidentifiedScenes: 8 }, "StashDB");
  assert.match(v3, /^8 of your scenes have no StashDB id/); // count + provider named
  assert.match(v3, /Identify them in Cove/); // states the next step (guided fix, not error)

  // The provider name is not hardcoded — v2 threads ThePornDB through unchanged.
  const v2 = guidedSetupSummary({ totalScenes: 100, unidentifiedScenes: 3 }, "ThePornDB");
  assert.match(v2, /no ThePornDB id/);

  // An empty provider falls back to the v3 default so the message is never provider-less.
  assert.match(guidedSetupSummary({ totalScenes: 1, unidentifiedScenes: 1 }, ""), /no StashDB id/);
});

test("GUIDED_SETUP_HEADING is the design-locked advisory heading", () => {
  assert.equal(GUIDED_SETUP_HEADING, "Some scenes can't be reconciled");
});

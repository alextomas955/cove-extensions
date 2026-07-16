/**
 * Behavior contract for the pure scene-status logic. The runner compiles sceneStatusLogic.ts and passes the
 * compiled module path in SCENE_STATUS_LOGIC_MODULE; importing the exact compiled artifact keeps the test
 * honest about what ships. Mirrors monitor-logic.test.mjs in shape.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.SCENE_STATUS_LOGIC_MODULE);
const {
  SCENE_STATE_LABEL,
  LEGEND_ORDER,
  sceneDetailBody,
  sceneReleasesBody,
  stateBadge,
  qualityCutoffText,
} = mod;

test("SCENE_STATE_LABEL maps every state to its DESIGN legend wording", () => {
  assert.equal(SCENE_STATE_LABEL.downloaded, "Downloaded");
  assert.equal(SCENE_STATE_LABEL.monitored, "Monitored");
  assert.equal(SCENE_STATE_LABEL.unmonitored, "Unmonitored");
  assert.equal(SCENE_STATE_LABEL.notAdded, "Not added");
  assert.equal(SCENE_STATE_LABEL.excluded, "Excluded");
});

test("LEGEND_ORDER lists the four primary states in DESIGN order (Downloaded, Monitored, Not added, Excluded)", () => {
  assert.deepEqual([...LEGEND_ORDER], ["downloaded", "monitored", "notAdded", "excluded"]);
  // Unmonitored is an honest fifth state but is NOT in the primary legend row.
  assert.equal(LEGEND_ORDER.includes("unmonitored"), false);
});

test("sceneDetailBody / sceneReleasesBody shape the exact PascalCase { CoveId } wire body (server resolves identity)", () => {
  assert.deepEqual(sceneDetailBody(42), { CoveId: 42 });
  assert.deepEqual(sceneReleasesBody(7), { CoveId: 7 });
  // No caller-supplied remote id ever leaks into the body.
  const body = sceneDetailBody(1);
  assert.equal("RemoteId" in body, false);
  assert.equal("StashId" in body, false);
});

test("stateBadge returns a { label, iconKey } pair for every state (glyph + label, never color-only)", () => {
  for (const state of ["downloaded", "monitored", "unmonitored", "notAdded", "excluded"]) {
    const badge = stateBadge(state);
    assert.equal(badge.label, SCENE_STATE_LABEL[state]);
    assert.equal(typeof badge.iconKey, "string");
    assert.ok(badge.iconKey.length > 0);
  }
  // Distinct glyphs so states are distinguishable without color.
  const keys = ["downloaded", "monitored", "unmonitored", "notAdded", "excluded"].map(
    (s) => stateBadge(s).iconKey,
  );
  assert.equal(new Set(keys).size, keys.length);
});

test("qualityCutoffText renders the Whisparr-only quality/cutoff line, omitting the cutoff fragment when unknown", () => {
  assert.equal(
    qualityCutoffText({ quality: "WEB-DL 1080p", cutoffMet: true }),
    "WEB-DL 1080p · cutoff met",
  );
  assert.equal(
    qualityCutoffText({ quality: "WEB-DL 1080p", cutoffMet: false }),
    "WEB-DL 1080p · cutoff unmet",
  );
  // cutoffMet null (unknown) → the cutoff fragment is dropped, never guessed.
  assert.equal(qualityCutoffText({ quality: "WEB-DL 1080p", cutoffMet: null }), "WEB-DL 1080p");
});

test("qualityCutoffText degrades to null when quality is absent (the whole line is omitted)", () => {
  assert.equal(qualityCutoffText({ quality: null, cutoffMet: null }), null);
  assert.equal(qualityCutoffText({ quality: "", cutoffMet: true }), null);
});

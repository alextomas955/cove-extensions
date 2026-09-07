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
  FILE_INDICATOR,
  sceneDetailBody,
  sceneReleasesBody,
  stateBadge,
  qualityCutoffText,
} = mod;

test("SCENE_STATE_LABEL maps the four management states to their DESIGN wording (no 'downloaded')", () => {
  assert.equal(SCENE_STATE_LABEL.monitored, "Monitored");
  assert.equal(SCENE_STATE_LABEL.unmonitored, "Unmonitored");
  assert.equal(SCENE_STATE_LABEL.notAdded, "Not added");
  assert.equal(SCENE_STATE_LABEL.excluded, "Excluded");
  // "downloaded" is no longer a management state — file presence is the secondary FILE_INDICATOR.
  assert.equal("downloaded" in SCENE_STATE_LABEL, false);
  assert.equal(Object.keys(SCENE_STATE_LABEL).length, 4);
});

test("LEGEND_ORDER is the four management states, monitored-first, with no 'downloaded'", () => {
  assert.deepEqual([...LEGEND_ORDER], ["monitored", "unmonitored", "notAdded", "excluded"]);
  assert.equal(LEGEND_ORDER.includes("downloaded"), false);
});

test("FILE_INDICATOR is a SECONDARY { label, iconKey } descriptor, distinct from any primary-state glyph", () => {
  assert.equal(typeof FILE_INDICATOR.label, "string");
  assert.ok(FILE_INDICATOR.label.length > 0);
  assert.equal(typeof FILE_INDICATOR.iconKey, "string");
  assert.ok(FILE_INDICATOR.iconKey.length > 0);
  // The file dot's glyph must not collide with a primary state glyph (it is not a state).
  const stateKeys = ["monitored", "unmonitored", "notAdded", "excluded"].map(
    (s) => stateBadge(s).iconKey,
  );
  assert.equal(stateKeys.includes(FILE_INDICATOR.iconKey), false);
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
  for (const state of ["monitored", "unmonitored", "notAdded", "excluded"]) {
    const badge = stateBadge(state);
    assert.equal(badge.label, SCENE_STATE_LABEL[state]);
    assert.equal(typeof badge.iconKey, "string");
    assert.ok(badge.iconKey.length > 0);
  }
  // Distinct glyphs so states are distinguishable without color.
  const keys = ["monitored", "unmonitored", "notAdded", "excluded"].map(
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

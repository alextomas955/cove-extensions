/**
 * Behavior contract for the pure missing-id guard logic. The runner compiles identityGuardLogic.ts and passes
 * the compiled module path in IDENTITY_GUARD_LOGIC_MODULE; importing the exact compiled artifact keeps the test
 * honest about what ships. Copy is verbatim against 29-UI-SPEC.md § Missing-id Guard.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.IDENTITY_GUARD_LOGIC_MODULE);
const { providerNameFor, missingIdMessage } = mod;

test("providerNameFor: v2 → ThePornDB, v3 (and any other/absent) → StashDB", () => {
  assert.equal(providerNameFor(2), "ThePornDB");
  assert.equal(providerNameFor("2"), "ThePornDB");
  assert.equal(providerNameFor(3), "StashDB");
  assert.equal(providerNameFor("3"), "StashDB");
  assert.equal(providerNameFor(null), "StashDB");
  assert.equal(providerNameFor(undefined), "StashDB");
});

test("missingIdMessage is the design-locked pattern, provider- and entity-named", () => {
  assert.equal(
    missingIdMessage("scene", "StashDB"),
    "This scene has no StashDB id — identify it in Cove first so Whisparr can match it.",
  );
  assert.equal(
    missingIdMessage("studio", "StashDB"),
    "This studio has no StashDB id — identify it in Cove first so Whisparr can match it.",
  );
  assert.equal(
    missingIdMessage("performer", "ThePornDB"),
    "This performer has no ThePornDB id — identify it in Cove first so Whisparr can match it.",
  );
});

test("missingIdMessage falls back to the v3 default provider when none is supplied (never provider-less)", () => {
  assert.equal(
    missingIdMessage("scene", null),
    "This scene has no StashDB id — identify it in Cove first so Whisparr can match it.",
  );
  assert.equal(
    missingIdMessage("scene", ""),
    "This scene has no StashDB id — identify it in Cove first so Whisparr can match it.",
  );
});

test("the guard is a guided fix — it states the next step and never nudges a version migration", () => {
  const msg = missingIdMessage("studio", "StashDB");
  assert.equal(msg.includes("identify it in Cove first"), true);
  for (const verb of ["upgrade", "switch", "migrate", "v3", "v2"]) {
    assert.equal(msg.toLowerCase().includes(verb), false, `guard must not mention "${verb}"`);
  }
});

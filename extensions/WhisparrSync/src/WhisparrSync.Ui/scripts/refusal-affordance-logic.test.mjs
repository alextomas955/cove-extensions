/**
 * Behavior contract for the guarded-control affordance composition. The runner compiles refusalAffordanceLogic.ts
 * and passes the compiled module path in REFUSAL_AFFORDANCE_LOGIC_MODULE; importing the exact compiled artifact
 * keeps the test honest about what ships.
 *
 * WHOLE STRINGS, never containment. The defect class is a name that contains the right text and is still wrong
 * overall — a bare reason contains none of the control's name, a doubled separator contains both halves, and a
 * containment assertion is satisfied by either.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.REFUSAL_AFFORDANCE_LOGIC_MODULE);
const { guardedControl, appendReason, REASON_SEPARATOR } = mod;

const CAPABILITY = "Currently available on Whisparr v3 (Eros)";
const IDENTITY = "This scene has no StashDB id — identify it in Cove first so Whisparr can match it.";
const CONFIGURATION = "Needs the Quality profile setting (Add defaults)";

test("the separator is the spaced em dash the composing call sites already use", () => {
  assert.equal(REASON_SEPARATOR, " — ");
});

test("no reason at all leaves the control untouched", () => {
  const a = guardedControl({ name: "Monitor" });
  assert.equal(a.disabled, false);
  assert.equal(a.title, undefined);
  assert.equal(a.ariaLabel, "Monitor");
  assert.equal(a.reason, null);
  assert.equal(a.cause, null);
});

test("with no reason the accessible name is byte-identical to the control's own name", () => {
  assert.equal(guardedControl({ name: "Monitor" }).ariaLabel, "Monitor");
  assert.equal(
    guardedControl({ name: "Add to Whisparr", enabledTitle: "Add this scene to Whisparr" })
      .ariaLabel,
    "Add to Whisparr",
  );
});

test("an enabled title rides through untouched, and an absent one omits the attribute", () => {
  assert.equal(
    guardedControl({ name: "Monitor", enabledTitle: "Mark wanted" }).title,
    "Mark wanted",
  );
  assert.equal(guardedControl({ name: "Monitor", enabledTitle: "" }).title, undefined);
  assert.equal(guardedControl({ name: "Monitor", enabledTitle: null }).title, undefined);
});

test("a configuration reason disables, titles, and appends — it never replaces the name", () => {
  const a = guardedControl({ name: "Monitor", configurationReason: CONFIGURATION });
  assert.equal(a.disabled, true);
  assert.equal(a.title, CONFIGURATION);
  assert.equal(a.ariaLabel, `Monitor${REASON_SEPARATOR}${CONFIGURATION}`);
  assert.equal(a.reason, CONFIGURATION);
  assert.equal(a.cause, "configuration");
});

test("the composed accessible name STARTS WITH the control's own name", () => {
  for (const reason of [CAPABILITY, IDENTITY, CONFIGURATION]) {
    for (const name of ["Monitor", "Add to Whisparr", "Sync my library to Whisparr"]) {
      const a = guardedControl({ name, configurationReason: reason });
      assert.equal(a.ariaLabel.startsWith(name), true, `"${a.ariaLabel}" must start with "${name}"`);
    }
  }
});

test("capability outranks configuration, and the configuration reason appears in no field", () => {
  const a = guardedControl({
    name: "Monitor",
    capabilityReason: CAPABILITY,
    configurationReason: CONFIGURATION,
  });
  assert.equal(a.reason, CAPABILITY);
  assert.equal(a.cause, "capability");
  assert.equal(a.title, CAPABILITY);
  assert.equal(a.ariaLabel, `Monitor${REASON_SEPARATOR}${CAPABILITY}`);
  for (const field of [a.title, a.ariaLabel, a.reason]) {
    assert.equal(String(field).includes(CONFIGURATION), false);
  }
});

test("identity outranks configuration", () => {
  const a = guardedControl({
    name: "Add to Whisparr",
    identityReason: IDENTITY,
    configurationReason: CONFIGURATION,
  });
  assert.equal(a.reason, IDENTITY);
  assert.equal(a.cause, "identity");
  assert.equal(a.ariaLabel.includes(CONFIGURATION), false);
});

test("with all three present capability wins", () => {
  const a = guardedControl({
    name: "Monitor",
    capabilityReason: CAPABILITY,
    identityReason: IDENTITY,
    configurationReason: CONFIGURATION,
  });
  assert.equal(a.reason, CAPABILITY);
  assert.equal(a.cause, "capability");
});

test("busy disables and says nothing — a spinner is not a refusal", () => {
  const a = guardedControl({ name: "Monitor", enabledTitle: "Mark wanted", busy: true });
  assert.equal(a.disabled, true);
  assert.equal(a.ariaLabel, "Monitor");
  assert.equal(a.title, "Mark wanted");
  assert.equal(a.reason, null);
  assert.equal(a.cause, null);
});

test("busy alongside a reason changes nothing but the disabled it already implied", () => {
  const busy = guardedControl({ name: "Monitor", configurationReason: CONFIGURATION, busy: true });
  const idle = guardedControl({ name: "Monitor", configurationReason: CONFIGURATION });
  assert.deepEqual(busy, { ...idle, disabled: true });
  assert.equal(idle.disabled, true);
});

test("a blank or whitespace-only reason is absent, never a dangling separator", () => {
  for (const blank of ["", "   ", "\n\t"]) {
    const a = guardedControl({ name: "Monitor", configurationReason: blank });
    assert.equal(a.ariaLabel, "Monitor");
    assert.equal(a.disabled, false);
    assert.equal(a.reason, null);
    assert.equal(a.cause, null);
  }
});

test("appendReason with no reason returns the name unchanged", () => {
  assert.equal(appendReason("Monitor", null), "Monitor");
  assert.equal(appendReason("Monitor", undefined), "Monitor");
  assert.equal(appendReason("Monitor", ""), "Monitor");
  assert.equal(appendReason("Monitor", "   "), "Monitor");
});

test("appendReason is idempotent — composing twice does not double the tail", () => {
  const once = appendReason("Monitor", CONFIGURATION);
  assert.equal(once, `Monitor${REASON_SEPARATOR}${CONFIGURATION}`);
  assert.equal(appendReason(once, CONFIGURATION), once);
  assert.equal(appendReason(appendReason(once, CONFIGURATION), CONFIGURATION), once);
});

test("a blank name never yields a bare reason as the accessible name", () => {
  for (const blank of ["", "   "]) {
    assert.equal(appendReason(blank, CONFIGURATION), blank);
    assert.equal(guardedControl({ name: blank, configurationReason: CONFIGURATION }).ariaLabel, blank);
  }
});

test("a composed name splits back into its two halves on the exported separator", () => {
  const a = guardedControl({ name: "Monitor", configurationReason: CONFIGURATION });
  const at = a.ariaLabel.indexOf(REASON_SEPARATOR);
  assert.equal(a.ariaLabel.slice(0, at), "Monitor");
  assert.equal(a.ariaLabel.slice(at + REASON_SEPARATOR.length), CONFIGURATION);
});

test("setNotice: a permanent refusal never offers a retry, and outranks an outage", () => {
  const { setNotice } = mod;

  assert.deepEqual(setNotice({ outage: false, permanentRefusal: false }), {
    kind: null,
    offersRetry: false,
  });
  assert.deepEqual(setNotice({ outage: true, permanentRefusal: false }), {
    kind: "outage",
    offersRetry: true,
  });
  assert.deepEqual(setNotice({ outage: false, permanentRefusal: true }), {
    kind: "permanent",
    offersRetry: false,
  });
  // Both set: the stronger claim wins and the retry still goes away, which is the case a render site
  // reading two booleans in sequence would get wrong.
  assert.deepEqual(setNotice({ outage: true, permanentRefusal: true }), {
    kind: "permanent",
    offersRetry: false,
  });
});

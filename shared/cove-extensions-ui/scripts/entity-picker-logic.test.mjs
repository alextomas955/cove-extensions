/**
 * Behavior contract for the shared entity-picker subset. The runner compiles entityPickerLogic.ts and
 * passes the compiled module path in PICKER_LOGIC_MODULE; importing the exact compiled artifact keeps
 * the test honest about what ships. The entity-reference picker helpers that only Renamer uses are
 * tested in Renamer's own suite, not here.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.PICKER_LOGIC_MODULE);
const { availableOptions } = mod;

test("availableOptions offers only not-yet-picked options, in the canonical order", () => {
  const opts = [
    { value: "Male", label: "Male" },
    { value: "Female", label: "Female" },
    { value: "Intersex", label: "Intersex" },
  ];
  assert.deepEqual(
    availableOptions(opts, ["Female"]).map((o) => o.value),
    ["Male", "Intersex"],
  );
  assert.deepEqual(availableOptions(opts, []), opts);
  assert.deepEqual(availableOptions(opts, ["Male", "Female", "Intersex"]), []);
});

test("availableOptions does not mutate its input list", () => {
  const opts = [
    { value: "Male", label: "Male" },
    { value: "Female", label: "Female" },
  ];
  const snapshot = JSON.parse(JSON.stringify(opts));
  availableOptions(opts, ["Male"]);
  assert.deepEqual(opts, snapshot);
});

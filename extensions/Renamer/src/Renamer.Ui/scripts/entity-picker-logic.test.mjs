/**
 * Behavior contract for the pure entity-picker logic. The runner compiles entityPickerLogic.ts and
 * passes the compiled module path in PICKER_LOGIC_MODULE; importing the exact compiled artifact keeps
 * the test honest about what ships.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.PICKER_LOGIC_MODULE);
const {
  filterEntities,
  resolveStudioLabel,
  isResolvedStudioId,
  canonicalTagName,
  excludeEntities,
  availableOptions,
} = mod;

const studios = [
  { id: 10, name: "Alpha Studio" },
  { id: 20, name: "beta films" },
  { id: 30, name: "Gamma" },
  { id: 40, name: "Alphabet Pictures" },
];

test("a blank query returns the full list in server order", () => {
  assert.deepEqual(filterEntities("", studios), studios);
  assert.deepEqual(filterEntities("   ", studios), studios);
});

test("the filter matches case-insensitively as a substring", () => {
  assert.deepEqual(
    filterEntities("alph", studios).map((e) => e.name),
    ["Alpha Studio", "Alphabet Pictures"],
  );
});

test("the query is trimmed before comparing", () => {
  assert.deepEqual(
    filterEntities("  gamma  ", studios).map((e) => e.name),
    ["Gamma"],
  );
});

test("a query that matches nothing returns an empty list", () => {
  assert.deepEqual(filterEntities("zzz", studios), []);
});

test("a stored studio id present in the list resolves to its name", () => {
  assert.equal(resolveStudioLabel(20, studios), "beta films");
  assert.equal(isResolvedStudioId(20, studios), true);
});

test("a stored studio id absent from the list resolves to a removable missing marker", () => {
  assert.equal(resolveStudioLabel(999, studios), "#999 (missing)");
  assert.equal(isResolvedStudioId(999, studios), false);
});

test("a tag typed in a different casing canonicalizes to the library's stored spelling", () => {
  const tags = [
    { id: 1, name: "Outdoor" },
    { id: 2, name: "POV" },
  ];
  assert.equal(canonicalTagName("outdoor", tags), "Outdoor");
  assert.equal(canonicalTagName("  pov ", tags), "POV");
});

test("a tag the picker has not seen is stored as the trimmed typed name", () => {
  const tags = [{ id: 1, name: "Outdoor" }];
  assert.equal(canonicalTagName("  Brand New ", tags), "Brand New");
});

test("the filter does not mutate its input list", () => {
  const input = [...studios];
  const snapshot = JSON.parse(JSON.stringify(studios));
  filterEntities("alph", input);
  assert.deepEqual(input, snapshot);
});

test("excludeEntities drops rows whose mapped value is already used, by id", () => {
  assert.deepEqual(
    excludeEntities(studios, [10, 30], (e) => e.id).map((e) => e.id),
    [20, 40],
  );
});

test("excludeEntities drops rows by canonical name when the map keys on names", () => {
  const tags = [
    { id: 1, name: "Outdoor" },
    { id: 2, name: "POV" },
    { id: 3, name: "Solo" },
  ];
  assert.deepEqual(
    excludeEntities(tags, ["POV"], (e) => e.name).map((e) => e.name),
    ["Outdoor", "Solo"],
  );
});

test("excludeEntities with nothing excluded returns the full list unmutated", () => {
  const snapshot = JSON.parse(JSON.stringify(studios));
  const out = excludeEntities(studios, [], (e) => e.id);
  assert.deepEqual(out, studios);
  assert.deepEqual(studios, snapshot);
  out.length = 0; // a copy, not the input
  assert.deepEqual(studios, snapshot);
});

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

test("an empty fetched list resolves any stored value as missing without throwing", () => {
  // The list hasn't loaded (or failed): resolveStudioLabel must still produce a removable marker
  // rather than throwing, and a stored studio id must report as unresolved.
  assert.equal(resolveStudioLabel(42, []), "#42 (missing)");
  assert.equal(isResolvedStudioId(42, []), false);
  assert.deepEqual(filterEntities("anything", []), []);
  assert.equal(canonicalTagName("  Solo ", []), "Solo");
});

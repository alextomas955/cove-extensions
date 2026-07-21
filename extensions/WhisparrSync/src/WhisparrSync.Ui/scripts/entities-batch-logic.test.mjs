/**
 * Behavior contract for the pure entities-batch logic. The runner compiles entitiesBatchLogic.ts and passes
 * the compiled module path in ENTITIES_BATCH_LOGIC_MODULE; importing the exact compiled artifact keeps the
 * test honest about what ships. The gating here MUST mirror the C# EntityBatchOpSupported.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.ENTITIES_BATCH_LOGIC_MODULE);
const {
  entityBatchMenuItems,
  entitiesBatchBody,
  entityKindFromListType,
  opMutatesEntityStatus,
  entityStatusCacheKeys,
} = mod;

const ops = (kind, version) => entityBatchMenuItems(kind, version).map((i) => i.op);

test("v3 studio offers the full parity set (monitor×2 + unmonitor + addMissing + search + reflectOwned)", () => {
  const items = entityBatchMenuItems("studio", "v3");
  assert.deepEqual(
    items.map((i) => i.op),
    ["monitor", "monitor", "unmonitor", "addMissing", "search", "reflectOwned"],
  );
  const monitors = items.filter((i) => i.op === "monitor");
  assert.deepEqual(
    monitors.map((i) => i.scope),
    ["NewReleases", "AllScenes"],
  );
});

test("the all-scenes monitor item is escalation-clarified (queues the back-catalogue); new-releases stays plain", () => {
  const items = entityBatchMenuItems("studio", "v3");
  const allScenes = items.find((i) => i.op === "monitor" && i.scope === "AllScenes");
  const newReleases = items.find((i) => i.op === "monitor" && i.scope === "NewReleases");
  assert.ok(allScenes, "an all-scenes monitor item exists");
  assert.ok(newReleases, "a new-releases monitor item exists");
  // The escalation is spelled out in the label — it reads as queueing the existing back-catalogue.
  assert.equal(allScenes.label.toLowerCase().includes("all scenes"), true);
  assert.equal(allScenes.label.toLowerCase().includes("back-catalogue"), true);
  // New-releases-only stays the plain, non-escalating choice.
  assert.equal(newReleases.label.toLowerCase().includes("back-catalogue"), false);
});

test("v3 performer also offers the full set (performer monitor is v3-only, and it IS v3 here)", () => {
  assert.deepEqual(ops("performer", "v3"), [
    "monitor",
    "monitor",
    "unmonitor",
    "addMissing",
    "search",
    "reflectOwned",
  ]);
});

test("v2 studio: monitor/unmonitor/search + reflectOwned, but NOT add-all-missing (v3-only)", () => {
  const o = ops("studio", "v2");
  assert.ok(o.includes("monitor"));
  assert.ok(o.includes("unmonitor"));
  assert.ok(o.includes("search"));
  assert.ok(o.includes("reflectOwned"));
  assert.ok(!o.includes("addMissing"), "add-all-missing needs the v3 per-scene add");
});

test("v2 performer: no monitorable entity → only reflect-owned (version-agnostic file import)", () => {
  assert.deepEqual(ops("performer", "v2"), ["reflectOwned"]);
});

test("entitiesBatchBody shapes the PascalCase wire body", () => {
  assert.deepEqual(entitiesBatchBody("studio", "monitor", "AllScenes", [1, 2, 3]), {
    Kind: "studio",
    CoveEntityIds: [1, 2, 3],
    Op: "monitor",
    Scope: "AllScenes",
  });
});

test("entityKindFromListType maps the host's plural list keys to the singular wire kind", () => {
  assert.equal(entityKindFromListType("studios"), "studio");
  assert.equal(entityKindFromListType("performers"), "performer");
  assert.equal(entityKindFromListType("studio"), "studio");
  assert.equal(entityKindFromListType("videos"), null);
});

test("opMutatesEntityStatus is true for every status-changing op and false for search", () => {
  assert.equal(opMutatesEntityStatus("monitor"), true);
  assert.equal(opMutatesEntityStatus("unmonitor"), true);
  assert.equal(opMutatesEntityStatus("addMissing"), true);
  assert.equal(opMutatesEntityStatus("reflectOwned"), true);
  // search grabs but changes neither the monitored flag nor the present/total counts the badges read.
  assert.equal(opMutatesEntityStatus("search"), false);
});

test("entityStatusCacheKeys builds the byte-identical kind:id keys entityCardStatusStore caches on", () => {
  assert.deepEqual(entityStatusCacheKeys("studio", [1, 2]), ["studio:1", "studio:2"]);
  assert.deepEqual(entityStatusCacheKeys("performer", [42]), ["performer:42"]);
  assert.deepEqual(entityStatusCacheKeys("studio", []), []);
});

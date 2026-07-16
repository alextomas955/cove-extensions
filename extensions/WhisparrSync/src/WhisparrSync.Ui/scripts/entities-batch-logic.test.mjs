/**
 * Behavior contract for the pure entities-batch logic. The runner compiles entitiesBatchLogic.ts and passes
 * the compiled module path in ENTITIES_BATCH_LOGIC_MODULE; importing the exact compiled artifact keeps the
 * test honest about what ships. The gating here MUST mirror the C# EntityBatchOpSupported.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.ENTITIES_BATCH_LOGIC_MODULE);
const { entityBatchMenuItems, entitiesBatchBody, entityKindFromListType } = mod;

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

/**
 * Behavior contract for the pure scene-folder-overlap logic. The runner (check-scene-folder-overlap-logic.mjs)
 * compiles sceneFolderOverlapLogic.ts and passes the compiled module URL via SCENE_FOLDER_OVERLAP_LOGIC_MODULE.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.SCENE_FOLDER_OVERLAP_LOGIC_MODULE);
const {
  sceneFolderOverlapsFromServer,
  hasSceneFolderOverlap,
  doubledPathDisplay,
  sceneFolderOverlapSummary,
} = mod;

const ONE = { root: "/data/media/scenes", prefix: "scenes", suggestedRoot: "/data/media" };

test("sceneFolderOverlapsFromServer: reads a well-formed overlaps array", () => {
  const out = sceneFolderOverlapsFromServer({ overlaps: [ONE] });
  assert.deepEqual(out, [ONE]);
});

test("sceneFolderOverlapsFromServer: malformed top-level input yields []", () => {
  assert.deepEqual(sceneFolderOverlapsFromServer(null), []);
  assert.deepEqual(sceneFolderOverlapsFromServer("nope"), []);
  assert.deepEqual(sceneFolderOverlapsFromServer(42), []);
  assert.deepEqual(sceneFolderOverlapsFromServer({}), []); // missing overlaps
  assert.deepEqual(sceneFolderOverlapsFromServer({ overlaps: "x" }), []); // non-array
  assert.deepEqual(sceneFolderOverlapsFromServer({ overlaps: {} }), []); // non-array object
});

test("sceneFolderOverlapsFromServer: v2/no-overlap empty payload stays empty", () => {
  assert.deepEqual(sceneFolderOverlapsFromServer({ overlaps: [] }), []);
});

test("sceneFolderOverlapsFromServer: items missing/blanking any field are dropped", () => {
  const raw = {
    overlaps: [
      ONE, // kept
      null, // not an object
      "str", // not an object
      { root: "/a", prefix: "scenes" }, // missing suggestedRoot
      { root: "/a", prefix: "scenes", suggestedRoot: "" }, // empty suggestedRoot
      { root: "", prefix: "scenes", suggestedRoot: "/a" }, // empty root
      { root: "/a", prefix: "", suggestedRoot: "/a" }, // empty prefix
      { root: "/a", prefix: 7, suggestedRoot: "/b" }, // non-string prefix
      { root: "/data/x/movies", prefix: "movies", suggestedRoot: "/data/x" }, // kept
    ],
  };
  assert.deepEqual(sceneFolderOverlapsFromServer(raw), [
    ONE,
    { root: "/data/x/movies", prefix: "movies", suggestedRoot: "/data/x" },
  ]);
});

test("hasSceneFolderOverlap: true only for a non-empty list", () => {
  assert.equal(hasSceneFolderOverlap([ONE]), true);
  assert.equal(hasSceneFolderOverlap([]), false);
});

test("doubledPathDisplay: renders <root>/<prefix>/…", () => {
  assert.equal(doubledPathDisplay(ONE), "/data/media/scenes/scenes/…");
});

test("sceneFolderOverlapSummary: names the doubled path and the suggested-root fix", () => {
  const s = sceneFolderOverlapSummary(ONE);
  assert.ok(s.includes("/data/media/scenes/scenes/…"), "contains the doubled path");
  assert.ok(s.includes("/data/media"), "contains the suggestedRoot literal");
  assert.ok(s.includes("scenes"), "names the doubled segment");
  assert.match(s, /In Whisparr/); // guidance the user applies in Whisparr, not a command to the extension
});

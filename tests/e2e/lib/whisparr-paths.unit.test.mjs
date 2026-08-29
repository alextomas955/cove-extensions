// The containment rule that decides whether an instance's reported library root is one the host can
// resolve. It touches no network and no container, because the rule is a string comparison: a path
// the host has no root for does not match whether or not a file exists anywhere.
import { test } from "node:test";
import assert from "node:assert/strict";
import { libraryRootsContaining } from "./whisparr-fixture.mjs";

// Transcribed from the roots the harness's own host declares, never read out of it: an expectation
// computed from the thing it checks agrees with it forever and reports nothing.
const HOST_ROOTS = ["/data", "/data2"];

test("a path under a root is contained by that root alone", () => {
  assert.deepEqual(libraryRootsContaining("/data/whisparr/scene.mp4", HOST_ROOTS), ["/data"]);
});

test("a path equal to a root is contained by it", () => {
  assert.deepEqual(libraryRootsContaining("/data", HOST_ROOTS), ["/data"]);
});

test("a path under no root is contained by nothing", () => {
  assert.deepEqual(libraryRootsContaining("/media/whisparr", HOST_ROOTS), []);
});

test("a path under nested roots is contained by every one of them", () => {
  assert.deepEqual(libraryRootsContaining("/data/inner/scene.mp4", ["/data", "/data/inner"]), [
    "/data",
    "/data/inner",
  ]);
});

test("a root does not contain a sibling whose name merely extends its own", () => {
  assert.deepEqual(libraryRootsContaining("/data22/scene.mp4", HOST_ROOTS), []);
});

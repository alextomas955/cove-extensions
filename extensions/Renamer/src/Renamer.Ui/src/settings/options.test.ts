// The one thing the panel decides about a stored destination root: which library path it names. The
// stored spelling, the defaults and the conversions belong to the backend's `/options` tests.
import { test } from "vitest";
import assert from "node:assert/strict";

import { chosenLibraryPath } from "./options";

// The panel stores a destination root as the very string the library-path list gave it, then
// re-checks membership against a later reading of that list. Cove hands paths back in the platform's
// own spelling, so a root that arrives spelled differently must still name the same folder - a miss
// makes chosenLibraryPath return undefined, which is the state that skips the rule, so a user's
// destination silently stops applying.
test("a stored root still names its library path through a separator difference", () => {
  const libraryPaths = ["C:/Videos", "/data"];

  for (const stored of ["C:/Videos", "C:\\Videos", "C:/Videos/", "C:\\Videos\\"]) {
    assert.equal(chosenLibraryPath(stored, libraryPaths), "C:/Videos", `stored as ${stored}`);
  }

  assert.equal(chosenLibraryPath("/data/", libraryPaths), "/data");
});

// The library path may carry the trailing separator instead of the stored root, and one folder must
// still be one folder whichever side it is written on.
test("a trailing separator on the library path side is forgiven too", () => {
  assert.equal(chosenLibraryPath("C:/Videos", ["C:/Videos/"]), "C:/Videos/");
});

// Case is deliberately not forgiven: a converted root is the library path's own casing and a picked
// one is the string the endpoint gave, so folding case would invent a second opinion about when two
// paths name one folder, on a host whose case rule the panel cannot see.
test("case is not forgiven, so the panel never invents a case rule the host may not share", () => {
  assert.equal(chosenLibraryPath("c:/videos", ["C:/Videos"]), undefined);
});

// A root naming no library path is the badge state, and it must be distinguishable from a match.
test("a root naming no library path resolves to undefined", () => {
  assert.equal(chosenLibraryPath("D:/Elsewhere", ["C:/Videos"]), undefined);
});

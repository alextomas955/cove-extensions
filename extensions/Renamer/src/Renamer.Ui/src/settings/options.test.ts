// The one thing the panel decides about a stored destination root: which library path it names. The
// stored spelling, the defaults and the conversions belong to the backend's `/options` tests.
import { test } from "vitest";
import assert from "node:assert/strict";

import { chosenLibraryPath } from "./options";

const LIBRARY_PATHS = ["C:/Videos", "/data"];

test.each([
  // The panel stores a destination root as the very string the library-path list gave it, then
  // re-checks membership against a later reading of that list. Cove hands paths back in the
  // platform's own spelling, so a root that arrives spelled differently must still name the same
  // folder - a miss makes chosenLibraryPath return undefined, which is the state that skips the
  // rule, so a user's destination silently stops applying.
  ["a stored root names its library path", "C:/Videos", LIBRARY_PATHS, "C:/Videos"],
  ["a stored root in the other slash style names it too", "C:\\Videos", LIBRARY_PATHS, "C:/Videos"],
  ["a trailing separator on the stored root is forgiven", "C:/Videos/", LIBRARY_PATHS, "C:/Videos"],
  ["both differences at once are forgiven", "C:\\Videos\\", LIBRARY_PATHS, "C:/Videos"],
  ["a POSIX root is matched through its trailing separator", "/data/", LIBRARY_PATHS, "/data"],
  // The library path may carry the trailing separator instead of the stored root, and one folder must
  // still be one folder whichever side it is written on.
  [
    "a trailing separator on the library path side is forgiven too",
    "C:/Videos",
    ["C:/Videos/"],
    "C:/Videos/",
  ],
  // Case is deliberately not forgiven: a converted root is the library path's own casing and a picked
  // one is the string the endpoint gave, so folding case would invent a second opinion about when two
  // paths name one folder, on a host whose case rule the panel cannot see.
  [
    "case is not forgiven, so the panel never invents a case rule the host may not share",
    "c:/videos",
    ["C:/Videos"],
    undefined,
  ],
  // A root naming no library path is the badge state, and it must be distinguishable from a match.
  ["a root naming no library path resolves to undefined", "D:/Elsewhere", ["C:/Videos"], undefined],
] as const)("%s", (_name, stored, libraryPaths, named) => {
  assert.equal(chosenLibraryPath(stored, libraryPaths), named);
});

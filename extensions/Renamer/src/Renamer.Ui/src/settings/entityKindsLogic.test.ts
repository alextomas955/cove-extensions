// The store-or-drop rule for one per-kind entry. An entry that matches the defaults is dropped, so a
// setting turned on and back off leaves the blob as it started. A destination naming neither a root
// nor a folder is a real instruction and is stored.
import { test } from "vitest";
import assert from "node:assert/strict";

import { nextKinds, type KindMap } from "./entityKindsLogic";

test("a destination is stored on an enabled kind", () => {
  assert.deepEqual(nextKinds({}, "image", true, { root: "D:/images", template: "$studio" }), {
    image: { enabled: true, destination: { root: "D:/images", template: "$studio" } },
  });
});

test("a destination naming neither a root nor a folder is kept, not treated as absent", () => {
  // It is the instruction "rename in place, under the library path the file is already in", which is
  // a different answer from having no destination of its own.
  assert.deepEqual(nextKinds({}, "audio", true, { root: "", template: "" }), {
    audio: { enabled: true, destination: { root: "", template: "" } },
  });
});

test("editing one kind leaves the others alone", () => {
  const map: KindMap = { video: { enabled: false, destination: null } };

  assert.deepEqual(nextKinds(map, "text", false, null), {
    video: { enabled: false, destination: null },
    text: { enabled: false, destination: null },
  });
});

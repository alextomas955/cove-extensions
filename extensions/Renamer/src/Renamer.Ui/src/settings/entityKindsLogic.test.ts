/**
 * The store-or-drop rule for one per-kind entry. An entry that says nothing the defaults do not is
 * dropped, so a setting turned on and back off leaves the blob as it started; anything else is
 * stored, including a destination naming neither a root nor a folder, which is a real instruction.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import { kindSettings, nextKinds, type KindMap } from "./entityKindsLogic";

test("an absent entry reads as renamed with no folder of its own", () => {
  assert.deepEqual(kindSettings({}, "Text"), { Enabled: true, Destination: null });
});

test("turning a kind off stores an entry", () => {
  assert.deepEqual(nextKinds({}, "Text", false, null), {
    Text: { Enabled: false, Destination: null },
  });
});

test("turning it back on removes the entry rather than storing the defaults", () => {
  const map: KindMap = { Text: { Enabled: false, Destination: null } };

  assert.deepEqual(nextKinds(map, "Text", true, null), {});
});

test("a destination is stored on an enabled kind", () => {
  assert.deepEqual(nextKinds({}, "Image", true, { Root: "D:/images", Template: "$studio" }), {
    Image: { Enabled: true, Destination: { Root: "D:/images", Template: "$studio" } },
  });
});

test("a destination naming neither a root nor a folder is kept, not treated as absent", () => {
  // It is the instruction "rename in place, under the library path the file is already in", which is
  // a different answer from having no destination of its own.
  assert.deepEqual(nextKinds({}, "Audio", true, { Root: "", Template: "" }), {
    Audio: { Enabled: true, Destination: { Root: "", Template: "" } },
  });
});

test("editing one kind leaves the others alone", () => {
  const map: KindMap = { Video: { Enabled: false, Destination: null } };

  assert.deepEqual(nextKinds(map, "Text", false, null), {
    Video: { Enabled: false, Destination: null },
    Text: { Enabled: false, Destination: null },
  });
});

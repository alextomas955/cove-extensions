// The store-or-drop rule for one per-kind entry. An entry that matches the defaults is dropped, so a
// setting turned on and back off leaves the blob as it started. A destination naming neither a root
// nor a folder is a real instruction and is stored.
import { test } from "vitest";
import assert from "node:assert/strict";

import { kindSettings, kindsSummary, nextKinds, type KindMap } from "./entityKindsLogic";

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

// The server can write a present key with no value. `{"Kinds":{"Text":null}}` survives the store's
// repair, which does not reach inside a collection, and is served straight back - so the panel meets
// a null here however the generated wire type reads. A backend test pins the server half.
test("a kind entry with no value reads as unlisted, like an absent one", () => {
  const withNull = { text: null } satisfies KindMap;

  assert.deepEqual(kindSettings(withNull, "text"), { enabled: true, destination: null });
  assert.deepEqual(kindSettings({}, "text"), kindSettings(withNull, "text"));
  assert.deepEqual(kindsSummary(withNull), kindsSummary({}));
});

test("an edit sends back no null entry, so a save cannot restate one", () => {
  const next = nextKinds({ text: null }, "video", false, null);

  assert.deepEqual(next, { video: { enabled: false, destination: null } });
  assert.ok(!Object.values(next).includes(null as never));
});

// A kind this panel does not render still belongs to the stored document. Rebuilding the map from
// RENAMABLE_KINDS instead of from its own keys would delete it on an unrelated edit.
test("an edit keeps a kind the panel does not render", () => {
  const next = nextKinds({ text: { enabled: false, destination: null } }, "video", false, null);

  assert.deepEqual(next, {
    text: { enabled: false, destination: null },
    video: { enabled: false, destination: null },
  });
});

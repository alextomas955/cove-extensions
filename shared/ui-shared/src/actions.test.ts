// Only the route builder carries behavior here, so this pins the exact `/extensions/<id>/<route>`
// string every call site depends on.
import { test } from "vitest";
import assert from "node:assert/strict";

import { extensionApi } from "./actions";

test("extensionApi builds /extensions/<id>/<route>", () => {
  const api = extensionApi("com.alextomas955.renamer");
  assert.equal(api("preview"), "/extensions/com.alextomas955.renamer/preview");
  assert.equal(api("renamer"), "/extensions/com.alextomas955.renamer/renamer");
});

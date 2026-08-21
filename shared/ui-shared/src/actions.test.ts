/**
 * Behavior contract for the pure `actions` module. Only the route builder carries behavior (the types
 * are compile-time only), so this pins the exact `/extensions/<id>/<route>` string every migrated call
 * site now depends on.
 */
import { test } from "vitest";
import assert from "node:assert/strict";

import { extensionApi } from "./actions";

test("extensionApi builds /extensions/<id>/<route>", () => {
  const api = extensionApi("com.alextomas955.renamer");
  assert.equal(api("preview"), "/extensions/com.alextomas955.renamer/preview");
  assert.equal(api("renamer"), "/extensions/com.alextomas955.renamer/renamer");
});

test("extensionApi is bound per id and forwards a dynamic route unchanged", () => {
  const renamer = extensionApi("com.alextomas955.renamer");
  assert.equal(renamer("preview"), "/extensions/com.alextomas955.renamer/preview");
  const route = "monitor";
  assert.equal(renamer(route), "/extensions/com.alextomas955.renamer/monitor");
});

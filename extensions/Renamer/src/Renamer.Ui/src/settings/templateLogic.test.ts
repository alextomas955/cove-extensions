import { test } from "vitest";
import assert from "node:assert/strict";

import { templateUsesToken } from "./templateLogic";

test.each([
  [
    "a $$ pair is a literal $, and a name after it is still a token",
    "performers",
    "$$performers",
    true,
  ],
  ["a $$ pair followed by a space starts no token", "performers", "$$ performers", false],
  ["the token argument matches without regard to case", "PERFORMERS", "$performers", true],
  ["the template text matches without regard to case", "performers", "$PERFORMERS", true],
  ["a longer name in the template is not the shorter token", "date", "$dateFoo", false],
  ["an empty template uses no token", "tags", "", false],
  ["$studio in the template leaves $studioCode unused", "studioCode", "$studio", false],
  ["$studioCode in the template leaves $studio unused", "studio", "$studioCode", false],
])("%s", (_name, token, filenameTemplate, expected) => {
  assert.equal(templateUsesToken(token, filenameTemplate, ""), expected);
});

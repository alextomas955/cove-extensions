/**
 * Behavior contract for templateUsesToken. The runner compiles templateValidation.ts and passes
 * the compiled module path in TEMPLATE_VALIDATION_MODULE; importing the exact compiled artifact
 * keeps the test honest about what ships.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.TEMPLATE_VALIDATION_MODULE);
const { templateUsesToken } = mod;

test("a token wrapped in an optional group is detected in the filename template", () => {
  assert.equal(templateUsesToken("performers", "$title { - $performers}", ""), true);
});

test("a token present only in the folder template is detected", () => {
  assert.equal(templateUsesToken("performers", "$title", "{ - $performers}"), true);
});

test("a token absent from both templates is not detected", () => {
  assert.equal(templateUsesToken("performers", "$title", "$ext"), false);
});

test("the $$ literal-escape pair does not false-positive", () => {
  assert.equal(templateUsesToken("performers", "$$performers", ""), false);
});

test("matching is case-insensitive on both the token argument and the template text", () => {
  assert.equal(templateUsesToken("PERFORMERS", "$performers", ""), true);
  assert.equal(templateUsesToken("performers", "$PERFORMERS", ""), true);
});

test("a longer token name does not false-positive-match a shorter target", () => {
  assert.equal(templateUsesToken("date", "$dateFoo", ""), false);
});

test("both templates empty returns false", () => {
  assert.equal(templateUsesToken("tags", "", ""), false);
});

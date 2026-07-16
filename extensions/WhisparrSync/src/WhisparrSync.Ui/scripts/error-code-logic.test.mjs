/**
 * Behavior contract for the pure error-body decoding. The runner (check-error-code-logic.mjs) compiles
 * errorCodeLogic.ts and passes the compiled module URL via ERROR_CODE_LOGIC_MODULE.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.ERROR_CODE_LOGIC_MODULE);
const { errorCode, isVersionUnsupportedBody, VERSION_UNSUPPORTED } = mod;

test("errorCode: reads a string code; null for absent/non-string/non-JSON bodies", () => {
  assert.equal(errorCode('{"code":"VERSION_UNSUPPORTED"}'), "VERSION_UNSUPPORTED");
  assert.equal(errorCode('{"code":"SOMETHING_ELSE","detail":"x"}'), "SOMETHING_ELSE");
  assert.equal(errorCode("{}"), null); // no code field
  assert.equal(errorCode('{"code":42}'), null); // code is not a string
  assert.equal(errorCode("not json at all"), null); // gateway/plain-text body
  assert.equal(errorCode(""), null); // empty body
});

test("isVersionUnsupportedBody: true only for the exact version-mismatch code", () => {
  assert.equal(isVersionUnsupportedBody(`{"code":"${VERSION_UNSUPPORTED}"}`), true);
  assert.equal(isVersionUnsupportedBody('{"code":"version_unsupported"}'), false); // case-sensitive
  assert.equal(isVersionUnsupportedBody('{"code":"OTHER"}'), false);
  assert.equal(isVersionUnsupportedBody("503 Service Unavailable"), false);
  assert.equal(isVersionUnsupportedBody(""), false);
});

test("VERSION_UNSUPPORTED is the wire-pinned code the server emits", () => {
  assert.equal(VERSION_UNSUPPORTED, "VERSION_UNSUPPORTED");
});

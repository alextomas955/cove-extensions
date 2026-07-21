/**
 * Logic-guard: the client options model NEVER carries the API key. The runner compiles options.ts
 * (with connectionResult.ts for its type import) and hands the compiled module path in OPTIONS_MODULE;
 * importing the exact shipped artifact keeps the test honest. `optionsFromServer` is the boundary the panel
 * uses to build form state from the server response — it must read ONLY the safe fields and drop any key,
 * so a `hasApiKey` boolean is all the UI ever holds (the field renders empty with a "Key is set" pill).
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.OPTIONS_MODULE);
const { optionsFromServer, DEFAULT_OPTIONS } = mod;

test("the client options model has no api-key field, only hasApiKey", () => {
  assert.ok(!("ApiKey" in DEFAULT_OPTIONS), "DEFAULT_OPTIONS must not model ApiKey");
  assert.ok(!("apiKey" in DEFAULT_OPTIONS), "DEFAULT_OPTIONS must not model apiKey");
  assert.equal(typeof DEFAULT_OPTIONS.hasApiKey, "boolean");
});

test("optionsFromServer drops any key the server (wrongly) included and keeps hasApiKey", () => {
  const opts = optionsFromServer({
    BaseUrl: "http://localhost:6969",
    SelectedVersion: "v3",
    QualityProfileId: 4,
    hasApiKey: true,
    // A leaked key MUST NOT survive the projection.
    ApiKey: "leaked-secret",
    apiKey: "leaked-secret-2",
  });

  assert.ok(!("ApiKey" in opts), "the raw key must never be bound onto the client model");
  assert.ok(!("apiKey" in opts), "the raw key must never be bound onto the client model");
  assert.equal(opts.hasApiKey, true);
  assert.equal(opts.BaseUrl, "http://localhost:6969");
  assert.equal(opts.QualityProfileId, 4);
});

test("optionsFromServer falls back to defaults for a null / non-object payload", () => {
  const opts = optionsFromServer(null);
  assert.equal(opts.hasApiKey, false);
  assert.equal(opts.BaseUrl, "");
});

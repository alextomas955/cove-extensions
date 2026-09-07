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
    baseUrl: "http://localhost:6969",
    selectedVersion: "v3",
    hasApiKey: true,
    // A leaked key MUST NOT survive the projection.
    ApiKey: "leaked-secret",
    apiKey: "leaked-secret-2",
  });

  assert.ok(!("ApiKey" in opts), "the raw key must never be bound onto the client model");
  assert.ok(!("apiKey" in opts), "the raw key must never be bound onto the client model");
  assert.equal(opts.hasApiKey, true);
  assert.equal(opts.BaseUrl, "http://localhost:6969");
});

test("optionsFromServer falls back to defaults for a null / non-object payload", () => {
  const opts = optionsFromServer(null);
  assert.equal(opts.hasApiKey, false);
  assert.equal(opts.BaseUrl, "");
});

test("the client options model never carries a discovery key — Cove is the sole credential source", () => {
  assert.ok(!("StashDbApiKey" in DEFAULT_OPTIONS), "DEFAULT_OPTIONS must not model StashDbApiKey");
  assert.ok(!("TpdbApiKey" in DEFAULT_OPTIONS), "DEFAULT_OPTIONS must not model TpdbApiKey");
  assert.ok(!("hasStashDbKey" in DEFAULT_OPTIONS), "the removed hasStashDbKey boolean must not reappear");
  assert.ok(!("hasTpdbKey" in DEFAULT_OPTIONS), "the removed hasTpdbKey boolean must not reappear");

  const opts = optionsFromServer({
    baseUrl: "http://localhost:6969",
    selectedVersion: "v3",
    hasApiKey: true,
    // Any discovery key/boolean the server wrongly included MUST NOT survive the projection.
    hasStashDbKey: true,
    hasTpdbKey: true,
    StashDbApiKey: "leaked-stash",
    TpdbApiKey: "leaked-tpdb",
  });

  assert.ok(!("StashDbApiKey" in opts), "a raw StashDB key must never bind onto the client model");
  assert.ok(!("TpdbApiKey" in opts), "a raw TPDB key must never bind onto the client model");
  assert.ok(!("hasStashDbKey" in opts), "the removed hasStashDbKey boolean must not bind onto the model");
  assert.ok(!("hasTpdbKey" in opts), "the removed hasTpdbKey boolean must not bind onto the model");
});

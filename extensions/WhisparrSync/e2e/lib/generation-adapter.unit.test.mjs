// Run by path: `node --test extensions/WhisparrSync/e2e/lib/generation-adapter.unit.test.mjs`.
// The shared harness package's own unit-test script globs `lib/*.unit.test.mjs` under `tests/e2e`
// alone and does not reach this folder.
//
// No container and no Cove. What is covered here is what an end-to-end run cannot show: a green run
// never takes a reader's refusal path, and member-name equality across the two generations is not
// decidable by collection or by lint.
import assert from "node:assert/strict";
import { test } from "node:test";

import { adapterFor } from "./generation-adapter.mjs";

const refusing = {
  get: async () => ({ status: 502, text: "Bad Gateway from the proxy", json: undefined }),
};

test("both generations answer to the same member names", () => {
  assert.deepEqual(Object.keys(adapterFor("v2")).sort(), Object.keys(adapterFor("v3")).sort());
});

test("a reader handed a refused response names the route, the status and the body", async () => {
  await assert.rejects(
    () => adapterFor("v3").instanceState(refusing),
    (error) => {
      assert.match(error.message, /\/api\/v3\/notification/);
      assert.match(error.message, /502/);
      assert.match(error.message, /Bad Gateway from the proxy/);
      return true;
    },
  );
});

test("a reader handed a well-formed page answers with its rows", async () => {
  const records = [{ eventType: 1 }, { eventType: 3 }];
  const answering = { get: async () => ({ status: 200, json: { records, totalRecords: 2 } }) };
  assert.deepEqual(await adapterFor("v2").historyRows(answering), records);
});

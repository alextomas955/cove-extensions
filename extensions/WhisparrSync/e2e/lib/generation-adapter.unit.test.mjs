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
import { whisparrAcquisitionSurface } from "./whisparr-sync-fixtures.mjs";

const refusing = {
  get: async () => ({ status: 502, text: "Bad Gateway from the proxy", json: undefined }),
};

/**
 * A refusal carrying a body that looks like what was asked for.
 *
 * This is the case a shape check alone admits, and the only one that catches a reader checking the
 * shape and not the status. A reader fed garbage refuses either way, so a test that feeds it garbage
 * says nothing about which of the two it checked.
 */
const refusingWith = (body, text) => ({
  get: async () => ({ status: 503, text, json: body }),
});

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

test("a listing refused with an empty list is a refusal, not an empty catalogue", async () => {
  await assert.rejects(
    () => adapterFor("v3").declaredSceneIdentifier(refusingWith([], "[]")),
    (error) => {
      assert.match(error.message, /\/api\/v3\/movie/);
      assert.match(error.message, /503/);
      return true;
    },
  );
});

test("a history page refused with a well-formed page is a refusal, not a history of none", async () => {
  const page = { records: [], totalRecords: 0 };
  await assert.rejects(
    () => adapterFor("v2").historyRows(refusingWith(page, JSON.stringify(page))),
    (error) => {
      assert.match(error.message, /\/api\/v3\/history/);
      assert.match(error.message, /503/);
      return true;
    },
  );
});

test("a roster refused with no JSON is not an instance that was asked to search nothing", async () => {
  await assert.rejects(
    () => adapterFor("v3").activity(refusingWith(undefined, "Service Unavailable")),
    (error) => {
      assert.match(error.message, /\/api\/v3\/command/);
      assert.match(error.message, /503/);
      assert.match(error.message, /Service Unavailable/);
      return true;
    },
  );
});

// Not the adapter's own member, and here because it is the bound every never-searched claim in this
// folder is taken against: read as an acquisition surface of none, a refusal makes a press this
// suite refuses to make on a real instance look safe.
test("the acquisition surface refused with no JSON is not an instance holding none", async () => {
  await assert.rejects(
    () => whisparrAcquisitionSurface(refusingWith(undefined, "Service Unavailable")),
    (error) => {
      assert.match(error.message, /\/api\/v3\/indexer/);
      assert.match(error.message, /503/);
      return true;
    },
  );
});

test("a reader handed a well-formed page answers with its rows", async () => {
  const records = [{ eventType: 1 }, { eventType: 3 }];
  const answering = { get: async () => ({ status: 200, json: { records, totalRecords: 2 } }) };
  assert.deepEqual(await adapterFor("v2").historyRows(answering), records);
});

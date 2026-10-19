import { test } from "node:test";
import assert from "node:assert/strict";

import { classifyRoute, judgeReflectOwnedOnV2 } from "./row-16-v2-reflect-owned-routes.mjs";

const VERDICTS = new Set([
  "v2-serves-all-three-reflect-owned-routes",
  "v2-serves-none-of-the-three-reflect-owned-routes",
  "inconclusive",
]);

const generation = (hardlink, importable, attach) => ({
  hardlinkSetting: { classification: hardlink },
  importableFiles: { classification: importable },
  attachOwnedFiles: { classification: attach },
});

const allServed = () => generation("served", "served", "served");
const noneServed = () => generation("not-served", "not-served", "not-served");

test("a call that never connected settles nothing", () => {
  assert.equal(classifyRoute({ status: 0, transportError: "connection refused" }), "indeterminate");
  assert.equal(classifyRoute({ status: 0 }), "indeterminate");
});

test("a route the instance does not mount is not served", () => {
  assert.equal(classifyRoute({ status: 404 }), "not-served");
  assert.equal(classifyRoute({ status: 405 }), "not-served");
});

test("a refusal naming the request as unknown is not served, whatever its status", () => {
  assert.equal(classifyRoute({ status: 400, namesTheRequestAsUnknown: true }), "not-served");
});

test("a success is served only when it carries what the product reads", () => {
  assert.equal(classifyRoute({ status: 200, answersWhatTheProductReads: true }), "served");
  assert.equal(classifyRoute({ status: 201, answersWhatTheProductReads: true }), "served");
  assert.equal(classifyRoute({ status: 200, answersWhatTheProductReads: false }), "indeterminate");
});

test("any other refusal settles nothing", () => {
  assert.equal(classifyRoute({ status: 400 }), "indeterminate");
  assert.equal(classifyRoute({ status: 401 }), "indeterminate");
  assert.equal(classifyRoute({ status: 500 }), "indeterminate");
});

test("a control that did not come out clean leaves the run inconclusive", () => {
  assert.equal(
    judgeReflectOwnedOnV2({ v2: allServed(), v3: generation("served", "indeterminate", "served") }),
    "inconclusive",
  );
  assert.equal(
    judgeReflectOwnedOnV2({ v2: noneServed(), v3: generation("served", "not-served", "served") }),
    "inconclusive",
  );
});

test("a mixed v2 is inconclusive, not an absence", () => {
  assert.equal(
    judgeReflectOwnedOnV2({
      v2: generation("served", "not-served", "not-served"),
      v3: allServed(),
    }),
    "inconclusive",
  );
  assert.equal(
    judgeReflectOwnedOnV2({
      v2: generation("not-served", "not-served", "indeterminate"),
      v3: allServed(),
    }),
    "inconclusive",
  );
});

test("a route the run could not drive at all leaves the verdict inconclusive", () => {
  assert.equal(
    judgeReflectOwnedOnV2({
      v2: generation("served", "served", "indeterminate"),
      v3: allServed(),
    }),
    "inconclusive",
  );
});

test("a route missing from the observations is not read as an absence", () => {
  assert.equal(
    judgeReflectOwnedOnV2({
      v2: { hardlinkSetting: { classification: "not-served" } },
      v3: allServed(),
    }),
    "inconclusive",
  );
});

test("the two decided verdicts need every route to agree", () => {
  assert.equal(
    judgeReflectOwnedOnV2({ v2: allServed(), v3: allServed() }),
    "v2-serves-all-three-reflect-owned-routes",
  );
  assert.equal(
    judgeReflectOwnedOnV2({ v2: noneServed(), v3: allServed() }),
    "v2-serves-none-of-the-three-reflect-owned-routes",
  );
});

test("no combination of classifications answers outside the closed set", () => {
  const kinds = ["served", "not-served", "indeterminate"];
  for (const a of kinds) {
    for (const b of kinds) {
      for (const c of kinds) {
        assert.ok(
          VERDICTS.has(judgeReflectOwnedOnV2({ v2: generation(a, b, c), v3: allServed() })),
          `v2 ${a}/${b}/${c} answered outside the closed set`,
        );
      }
    }
  }
});

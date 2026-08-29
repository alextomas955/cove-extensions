// The two decisions the bring-up makes that a container cannot be spared to demonstrate: what a
// teardown does when one stop throws, and what settles a provider read-back.
import { test } from "node:test";
import assert from "node:assert/strict";

import { describeServers } from "../../lib/cove-providers.mjs";
import { judgeReadBack, runEveryStop } from "./context.mjs";

const CONFIGURED = describeServers([
  {
    endpoint: "https://stashdb.example/graphql",
    apiKey: "aaaa",
    name: "stashdb",
    maxRequestsPerMinute: 240,
  },
]);

test("runEveryStop runs the later stops after an earlier one throws", async () => {
  const ran = [];
  const failures = await runEveryStop([
    async () => {
      ran.push("support");
      throw new Error("support refused");
    },
    async () => {
      ran.push("whisparr");
      throw new Error("whisparr refused");
    },
    async () => {
      ran.push("harness");
    },
  ]);
  assert.deepEqual(ran, ["support", "whisparr", "harness"]);
  assert.deepEqual(
    failures.map((cause) => cause.message),
    ["support refused", "whisparr refused"],
  );
});

test("runEveryStop reports nothing when every stop succeeds", async () => {
  assert.deepEqual(await runEveryStop([async () => {}, async () => {}]), []);
});

test("a read-back carrying entries that are not the configured ones has not settled", () => {
  const judged = judgeReadBack(CONFIGURED, [
    { endpoint: "https://stashdb.example/graphql", name: "stashdb", maxRequestsPerMinute: 240 },
  ]);
  assert.equal(judged.verdict, "partially-bound");
  assert.equal(judged.bound, false);
  assert.deepEqual(
    judged.mismatches.map((mismatch) => mismatch.field),
    ["apiKey.chars"],
  );
});

test("an empty read-back has not settled", () => {
  const judged = judgeReadBack(CONFIGURED, undefined);
  assert.equal(judged.verdict, "not-bound");
  assert.equal(judged.bound, false);
  assert.deepEqual(judged.described, []);
});

test("a read-back carrying the configured entries settles, described", () => {
  const judged = judgeReadBack(CONFIGURED, [
    {
      endpoint: "https://stashdb.example/graphql",
      apiKey: "zzzz",
      name: "stashdb",
      maxRequestsPerMinute: 240,
    },
  ]);
  assert.equal(judged.bound, true);
  assert.deepEqual(judged.mismatches, []);
  assert.equal(JSON.stringify(judged.described).includes("zzzz"), false);
});

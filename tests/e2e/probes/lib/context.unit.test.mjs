// The decisions the bring-up makes that a container cannot be spared to demonstrate.
import { test } from "node:test";
import assert from "node:assert/strict";

import { runEveryStop } from "./context.mjs";

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

import { test, expect } from "vitest";

import { BATCH_MENU_ROWS } from "./batchMenuLogic";

const keys = (): string[] => BATCH_MENU_ROWS.map((row) => row.key);

test("offers five rows, safest first and the row that changes future acceptance last", () => {
  expect(keys()).toEqual(["add", "monitor", "unmonitor", "search", "exclude"]);
});

test("names the verb the route reads, in the wire spelling, once per row", () => {
  expect(BATCH_MENU_ROWS.map((row) => row.verb)).toEqual([
    "add",
    "monitor",
    "unmonitor",
    "search",
    "exclude",
  ]);
});

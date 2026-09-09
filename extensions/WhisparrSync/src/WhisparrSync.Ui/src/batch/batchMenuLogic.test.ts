/**
 * Which rows the batch overlay offers, and in which order.
 *
 * The ORDER is the property under test rather than the membership: a set comparison passes a
 * re-sorted menu, and the order is what says which row is safest and which one spends.
 */
import { readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { test, expect } from "vitest";

import * as copy from "../common/ui/copy";
import { BATCH_MENU_ROWS } from "./batchMenuLogic";

const MODULE_SOURCE = readFileSync(
  path.resolve(path.dirname(fileURLToPath(import.meta.url)), "batchMenuLogic.ts"),
  "utf8",
);

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

test("sorts nothing, so two reads answer the same order", () => {
  expect(MODULE_SOURCE).not.toContain(".sort(");
  expect(MODULE_SOURCE).not.toContain(".reverse(");
  expect(keys()).toEqual(keys());
});

test("carries a declared label constant on every row", () => {
  expect(BATCH_MENU_ROWS.map((row) => row.label)).toEqual([
    copy.MENU_ADD,
    copy.MENU_MONITOR,
    copy.MENU_UNMONITOR,
    copy.SCENE_SEARCH,
    copy.MENU_EXCLUDE,
  ]);
});

test("carries a name and a verb per row and nothing else", () => {
  for (const row of BATCH_MENU_ROWS) {
    expect(Object.keys(row).toSorted(), row.key).toEqual(["key", "label", "verb"]);
  }
});

test("puts the one row that can download fourth, where the order says it belongs", () => {
  expect(keys()[3]).toBe("search");
  expect(BATCH_MENU_ROWS[3].label).toBe(copy.SCENE_SEARCH);
});

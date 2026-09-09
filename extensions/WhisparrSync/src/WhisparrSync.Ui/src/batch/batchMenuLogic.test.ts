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
    copy.SCENE_ADD,
    copy.MONITOR_IN_WHISPARR,
    copy.STOP_MONITORING_IN_WHISPARR,
    copy.SCENE_SEARCH,
    copy.SCENE_EXCLUDE,
  ]);
});

test("states what every row does, in declared sentences", () => {
  expect(BATCH_MENU_ROWS.map((row) => row.sentences)).toEqual([
    [copy.BATCH_ADD_STATES],
    [copy.BATCH_MONITOR_STATES],
    [copy.BATCH_UNMONITOR_STATES],
    [copy.BATCH_SEARCH_STATES],
    [copy.BATCH_EXCLUDE_STATES],
  ]);
});

test("names exactly one row as the only one that can download", () => {
  const downloading = BATCH_MENU_ROWS.filter((row) =>
    row.sentences.some((sentence) => sentence.includes("the only row here that can download")),
  );

  expect(downloading.map((row) => row.key)).toEqual(["search"]);
  expect(keys()[3]).toBe("search");
});

test("says on the exclude row where a single exclusion is taken back off the list", () => {
  const exclude = BATCH_MENU_ROWS.find((row) => row.key === "exclude");

  expect(exclude?.sentences.join(" ")).toContain("on that scene's own Whisparr tab");
});

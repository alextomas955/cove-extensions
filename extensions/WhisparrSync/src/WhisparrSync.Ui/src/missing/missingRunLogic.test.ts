import { expect, test } from "vitest";

import { runCovers, runIsUnderWay, runOver, RUN_OVER_EVERYTHING } from "./missingRunLogic";

test("a run over a selection covers the scenes it named and no other", () => {
  const run = runOver(["scene-a", "scene-b"]);

  expect(runCovers(run, "scene-a")).toBe(true);
  expect(runCovers(run, "scene-b")).toBe(true);
  expect(runCovers(run, "scene-c")).toBe(false);
});

// The narrowing the run was started under is the one the page is drawn from, so every card on
// screen is one it is working through.
test("a run over everything covers every card drawn", () => {
  expect(runCovers(RUN_OVER_EVERYTHING, "scene-a")).toBe(true);
  expect(runCovers(RUN_OVER_EVERYTHING, "any-scene-at-all")).toBe(true);
});

test("no run covers nothing, and is not under way", () => {
  expect(runCovers(null, "scene-a")).toBe(false);
  expect(runIsUnderWay(null)).toBe(false);
  expect(runIsUnderWay(runOver([]))).toBe(true);
  expect(runIsUnderWay(RUN_OVER_EVERYTHING)).toBe(true);
});

// The caller hands over the selection it holds, which it goes on to change.
test("the scenes a run names are held apart from the list it was given", () => {
  const selection = ["scene-a"];
  const run = runOver(selection);
  selection.push("scene-b");

  expect(runCovers(run, "scene-b")).toBe(false);
});

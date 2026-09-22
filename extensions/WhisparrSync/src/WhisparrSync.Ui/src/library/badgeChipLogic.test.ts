import { expect, test } from "vitest";

import { badgeChipFor } from "./badgeChipLogic";

const AT_REST = { running: false, settled: true, state: null, pageRefused: false } as const;

// What is happening now outranks what was last read: the state on screen describes a moment the run
// is in the middle of leaving.
test("a card a run is working through says so, whatever was last read", () => {
  expect(badgeChipFor({ ...AT_REST, running: true })).toBe("working");
  expect(badgeChipFor({ ...AT_REST, running: true, state: "monitored" })).toBe("working");
  expect(badgeChipFor({ ...AT_REST, running: true, settled: false })).toBe("working");
});

test("a card the instance answered for draws that state", () => {
  expect(badgeChipFor({ ...AT_REST, state: "monitored" })).toBe("state");
  expect(badgeChipFor({ ...AT_REST, state: "notAdded" })).toBe("state");
});

// The library holds no link this generation could name the entity by, so it was never asked. Drawn
// blank, the card reads as one the product forgot.
test("a settled read that answered no state says the entity is not linked", () => {
  expect(badgeChipFor(AT_REST)).toBe("notLinked");
});

// An unsettled read has established nothing, and a reason drawn there would be a claim about an
// answer that has not arrived.
test("a read still in flight draws nothing at all", () => {
  expect(badgeChipFor({ ...AT_REST, settled: false })).toBeNull();
});

// A page nothing answered for establishes nothing about any card. Drawn as not linked, a card would
// report a read that failed as a fact about what the library holds.
test("a card on a page nothing answered for draws nothing", () => {
  expect(badgeChipFor({ ...AT_REST, pageRefused: true })).toBeNull();
});

// What the instance last answered still stands: the card is reporting a state that was read, not
// claiming the failed read succeeded.
test("a refused page still draws a state that was already read", () => {
  expect(badgeChipFor({ ...AT_REST, pageRefused: true, state: "monitored" })).toBe("state");
});

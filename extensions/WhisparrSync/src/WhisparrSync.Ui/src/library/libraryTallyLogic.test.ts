// Each expectation is written out, not derived from the module: a count computed the same way
// twice agrees with itself whatever it is counting.
import { expect, test } from "vitest";

import { tallyReadings } from "./libraryTallyLogic";

test("the four states partition the cards that were answered for", () => {
  const tally = tallyReadings([
    { excluded: false, present: true, monitored: true },
    { excluded: false, present: true, monitored: false },
    { excluded: false, present: false, monitored: null },
    { excluded: true, present: false, monitored: null },
  ]);

  expect(tally.states).toEqual({
    monitored: 1,
    unmonitored: 1,
    notAdded: 1,
    excluded: 1,
    statusUnknown: 0,
  });
  expect(tally.counted).toBe(4);
});

test("a card nothing can be said for is counted nowhere at all", () => {
  const tally = tallyReadings([null, null, { excluded: false, present: true, monitored: true }]);

  expect(tally.counted).toBe(1);
  expect(tally.states.statusUnknown).toBe(0);
});

test("a card the instance answered nothing usable for is counted as unknown, not as absent", () => {
  const tally = tallyReadings([{ excluded: false, present: null, monitored: null }]);

  expect(tally.states.statusUnknown).toBe(1);
  expect(tally.states.notAdded).toBe(0);
  expect(tally.counted).toBe(1);
});

test("a file is counted beside the states rather than among them", () => {
  const tally = tallyReadings([
    { excluded: false, present: true, monitored: true, inLibrary: true },
    { excluded: false, present: true, monitored: false, inLibrary: true },
  ]);

  expect(tally.inLibrary).toBe(2);
  expect(tally.states.monitored).toBe(1);
  expect(tally.states.unmonitored).toBe(1);
});

test("a file nothing established is not counted as one the instance does not hold", () => {
  const tally = tallyReadings([
    { excluded: false, present: true, monitored: true },
    { excluded: false, present: true, monitored: true, inLibrary: null },
  ]);

  expect(tally.inLibrary).toBe(0);
  expect(tally.counted).toBe(2);
});

test("nothing on the page is every count at zero", () => {
  const tally = tallyReadings([]);

  expect(tally.counted).toBe(0);
  expect(tally.inLibrary).toBe(0);
  expect(Object.values(tally.states)).toEqual([0, 0, 0, 0, 0]);
});

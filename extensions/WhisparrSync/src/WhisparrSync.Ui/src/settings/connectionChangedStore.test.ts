import { expect, test } from "vitest";

import { announceConnectionChanged, onConnectionChanged } from "./connectionChangedStore";

test("an announcement reaches every listener until it unsubscribes", () => {
  let first = 0;
  let second = 0;
  const stopFirst = onConnectionChanged(() => first++);
  const stopSecond = onConnectionChanged(() => second++);

  announceConnectionChanged();
  stopFirst();
  announceConnectionChanged();
  stopSecond();
  announceConnectionChanged();

  expect([first, second]).toEqual([1, 2]);
});

// A section that unsubscribes while the announcement is being delivered must not skip the next
// listener, which is what iterating the live set would do.
test("a listener that unsubscribes during delivery does not skip the others", () => {
  const heard: string[] = [];
  const stopSelf = onConnectionChanged(() => {
    heard.push("first");
    stopSelf();
  });
  const stopSecond = onConnectionChanged(() => heard.push("second"));

  announceConnectionChanged();
  stopSecond();

  expect(heard).toEqual(["first", "second"]);
});

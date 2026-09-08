/**
 * The shared boolean's two promises: it starts off, and separately created subscribers observe one
 * flip.
 *
 * Off by default is what keeps a card identical to a card with no extension registered. One shared
 * value is what lets the control in a toolbar reach the badges on the cards below it, which are
 * separate host slot instances with no React tree in common.
 *
 * The store is at module scope, so each test leaves the boolean as it found it.
 */
import { afterEach, expect, test } from "vitest";

import { libraryStatusOn, subscribeLibraryStatus, toggleLibraryStatus } from "./libraryToggleStore";

afterEach(() => {
  if (libraryStatusOn()) toggleLibraryStatus();
});

/** One subscriber, counting what it was told. */
function observe(): { seen: () => number; unsubscribe: () => void } {
  let changes = 0;
  const unsubscribe = subscribeLibraryStatus(() => {
    changes += 1;
  });
  return { seen: () => changes, unsubscribe };
}

test("the badges start hidden, so a card is unchanged until someone asks", () => {
  expect(libraryStatusOn()).toBe(false);
});

test("two subscribers created independently both observe one flip", () => {
  const first = observe();
  const second = observe();

  toggleLibraryStatus();

  expect(libraryStatusOn()).toBe(true);
  expect(first.seen(), "the first subscriber was not told").toBe(1);
  expect(second.seen(), "the second subscriber was not told").toBe(1);

  first.unsubscribe();
  second.unsubscribe();
});

test("unsubscribing one leaves the other observing", () => {
  const staying = observe();
  const leaving = observe();
  leaving.unsubscribe();

  toggleLibraryStatus();

  expect(staying.seen(), "the remaining subscriber stopped being told").toBe(1);
  expect(leaving.seen(), "an unsubscribed listener was told anyway").toBe(0);

  staying.unsubscribe();
});

test("flipping twice returns to hidden", () => {
  toggleLibraryStatus();
  toggleLibraryStatus();

  expect(libraryStatusOn()).toBe(false);
});

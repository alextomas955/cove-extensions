// The rule that keeps this row's record free of header VALUES, exercised without a container.
//
// The row's own run cannot fail on this against a fixture that behaves: the listener reports the
// headers a well-behaved instance sent, and every one of those is a plain name. So the case worth
// asserting is the one a real run does not reach - a captured entry that is not a name - and it is
// asserted here rather than left to a future delivery to discover.
import { test } from "node:test";
import assert from "node:assert/strict";

import { headerNames } from "./row-15-import-event-payloads.mjs";

// Transcribed from the names the listener recorded on both generations, by hand from the probe
// record. A set read back out of the row would agree with the row whatever either said.
const OBSERVED = {
  Host: "listener:8099",
  Connection: "close",
  Accept: "application/json",
  "Accept-Encoding": "gzip, br",
  "Content-Type": "application/json",
  "Content-Length": "3158",
  "User-Agent": "Whisparr/3.3.8.1097 (alpine 3.23.5)",
};

test("a delivery's headers reach the record as names, sorted, and never as values", () => {
  const names = headerNames(OBSERVED);

  assert.deepEqual(names, [
    "Accept",
    "Accept-Encoding",
    "Connection",
    "Content-Length",
    "Content-Type",
    "Host",
    "User-Agent",
  ]);
  for (const value of Object.values(OBSERVED)) {
    assert.ok(!names.includes(value), `${value} reached the record`);
  }
});

test("no headers at all is an empty list rather than a throw", () => {
  assert.deepEqual(headerNames(undefined), []);
  assert.deepEqual(headerNames({}), []);
});

// The three shapes a name-and-value pair arrives in when a capture is folded in wholesale by a later
// edit. Each must stop the record being written rather than land in it.
for (const entry of ["Host: listener:8099", "X Api Key", "Authorization=Basic abc"]) {
  test(`an entry spelled "${entry}" is refused rather than recorded`, () => {
    assert.throws(
      () => headerNames({ [entry]: "" }),
      /must be a name and never a value/,
      `${entry} was accepted as a header name`,
    );
  });
}

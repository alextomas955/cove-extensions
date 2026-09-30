// The ladder's reading of a short page, which decides whether the row writes a gap candidate.
import { test } from "node:test";
import assert from "node:assert/strict";

import { summariseCeiling } from "./row-08-provider-paging.mjs";

const page = (requestedPerPage, returned, reportedTotal = null) => ({
  requestedPerPage,
  returned,
  reportedTotal,
});

test("a catalogue smaller than the page asked for is not an enforced ceiling", () => {
  const summary = summariseCeiling([page(25, 25, 30), page(100, 30, 30), page(1000, 30, 30)]);
  assert.equal(summary.enforcement, "not reached");
  assert.equal(summary.exhaustedTheCatalogue, true);
  assert.equal(summary.enforcedCeiling, null);
  assert.equal(summary.lowerBound, 30);
});

test("a short page the surface still had rows for is truncation", () => {
  const summary = summariseCeiling([
    page(25, 25, 5000),
    page(100, 100, 5000),
    page(1000, 100, 5000),
  ]);
  assert.equal(summary.enforcement, "truncates");
  assert.equal(summary.exhaustedTheCatalogue, false);
  assert.equal(summary.enforcedCeiling, 100);
  assert.equal(summary.lowerBound, null);
});

test("a short page from a surface reporting no total is read as truncation", () => {
  const summary = summariseCeiling([page(25, 25), page(1000, 100)]);
  assert.equal(summary.enforcement, "truncates");
  assert.equal(summary.exhaustedTheCatalogue, false);
  assert.equal(summary.enforcedCeiling, 100);
});

test("a ladder answered whole at every step reaches no ceiling", () => {
  const summary = summariseCeiling([page(25, 25, 5000), page(100, 100, 5000)]);
  assert.equal(summary.enforcement, "not reached");
  assert.equal(summary.exhaustedTheCatalogue, false);
  assert.equal(summary.lowerBound, 100);
  assert.equal(summary.largestRequested, 100);
});

test("a request the surface answered with no page at all is a refusal", () => {
  const summary = summariseCeiling([
    page(25, 25, 5000),
    { requestedPerPage: 1000, returned: null, reportedTotal: null },
  ]);
  assert.equal(summary.enforcement, "refuses");
  assert.equal(summary.enforcedCeiling, 25);
});

// The row's verdict, driven with observations rather than with an instance. Every input here is
// written by hand: a case computed from the row's own measurement would agree with it whatever
// either one said, and this verdict is read by a later plan as a gate.
import { test } from "node:test";
import assert from "node:assert/strict";

import { judgeExposure } from "./row-12-extension-data-exposure.mjs";

const observation = (overrides = {}) => ({
  write: { status: 200 },
  ownerRead: { status: 200, markerPresent: true },
  anonymousInNetworkRead: { status: 200, markerPresent: true, transportError: "" },
  absentExtensionControl: { status: 404 },
  ...overrides,
});

test("a marker the anonymous in-network caller received is the strongest outcome", () => {
  assert.equal(judgeExposure(observation()), "marker-returned-to-anonymous-in-network-caller");
});

test("a marker only the owner received is the middle outcome", () => {
  assert.equal(
    judgeExposure(
      observation({
        anonymousInNetworkRead: { status: 401, markerPresent: false, transportError: "" },
      }),
    ),
    "marker-returned-to-owner-only",
  );
});

test("a read that carried the marker to nobody says the route returned nothing", () => {
  assert.equal(
    judgeExposure(
      observation({
        ownerRead: { status: 200, markerPresent: false },
        anonymousInNetworkRead: { status: 200, markerPresent: false, transportError: "" },
      }),
    ),
    "route-returned-nothing-for-this-extension",
  );
});

test("a control that did not answer 404 establishes nothing about the route", () => {
  assert.equal(
    judgeExposure(observation({ absentExtensionControl: { status: 200 } })),
    "inconclusive",
  );
});

test("a refused write leaves the reads measuring an empty store", () => {
  assert.equal(judgeExposure(observation({ write: { status: 403 } })), "inconclusive");
});

test("a read that never arrived is not an absence", () => {
  assert.equal(
    judgeExposure(
      observation({
        anonymousInNetworkRead: {
          status: 0,
          markerPresent: false,
          transportError: "name does not resolve",
        },
      }),
    ),
    "inconclusive",
  );
});

test("an owner read the instance refused leaves the outcome unsettled", () => {
  assert.equal(
    judgeExposure(observation({ ownerRead: { status: 500, markerPresent: false } })),
    "inconclusive",
  );
});

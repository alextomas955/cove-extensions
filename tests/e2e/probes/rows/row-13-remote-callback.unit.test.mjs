// The row's verdict, driven with observations rather than with a container: the two cases it is
// wrong about are the ones no fixture reproduces on demand.
import { test } from "node:test";
import assert from "node:assert/strict";

import { classifyObservation, judgeBoundary } from "./row-13-remote-callback.mjs";

const route = (name, first, repeat) => ({ route: name, first, repeat });

test("a call that never arrived is unreached, not refused", () => {
  assert.equal(
    classifyObservation({ status: 0, transportError: "name does not resolve" }),
    "unreached",
  );
  assert.equal(classifyObservation({ status: 0 }), "unreached");
  assert.equal(classifyObservation({ status: 401 }), "refused");
  assert.equal(classifyObservation({ status: 403 }), "refused");
  assert.equal(classifyObservation({ status: 200 }), "allowed");
});

test("an unreached route leaves the boundary unsettled", () => {
  assert.equal(
    judgeBoundary([
      route("GET /health", "unreached", "unreached"),
      route("GET /api/system/config", "refused", "refused"),
    ]),
    "inconclusive",
  );
});

test("routes that disagree leave the boundary unsettled", () => {
  assert.equal(
    judgeBoundary([
      route("GET /health", "allowed", "refused"),
      route("GET /api/system/config", "refused", "allowed"),
    ]),
    "inconclusive",
  );
});

test("a verdict holds only when every route reports it", () => {
  assert.equal(
    judgeBoundary([route("a", "refused", "refused"), route("b", "refused", "refused")]),
    "refused-always",
  );
  assert.equal(
    judgeBoundary([route("a", "refused", "allowed"), route("b", "refused", "allowed")]),
    "refused-first-then-allowed",
  );
  assert.equal(
    judgeBoundary([route("a", "allowed", "allowed"), route("b", "allowed", "allowed")]),
    "allowed-always",
  );
});

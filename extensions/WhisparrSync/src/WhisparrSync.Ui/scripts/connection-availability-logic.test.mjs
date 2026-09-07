/**
 * Behavior contract for the pure connection-availability copy logic. The runner
 * (check-connection-availability-logic.mjs) compiles connectionAvailabilityLogic.ts and passes the
 * compiled module URL via CONNECTION_AVAILABILITY_LOGIC_MODULE.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.CONNECTION_AVAILABILITY_LOGIC_MODULE);
const { notLoadedMessage } = mod;

test("first-run copy names the subject and asks the user to Test the connection", () => {
  const msg = notLoadedMessage(false, "Whisparr's file settings");
  assert.equal(msg, "Test the connection to load Whisparr's file settings.");
  // Subject is interpolated verbatim so each section keeps its own noun.
  assert.match(notLoadedMessage(false, "your library preview"), /your library preview/);
});

test("unreachable copy is subject-independent and points at retry — never implies first-time setup", () => {
  const msg = notLoadedMessage(true, "Whisparr's file settings");
  // The distinction that fixes the reported bug: a configured-but-down connection must not read as
  // "you still have to connect". It says unreachable + retry, and drops the per-subject noun.
  assert.match(msg, /isn't reachable right now/);
  assert.match(msg, /Test connection above to retry/);
  assert.doesNotMatch(msg, /file settings/);
});

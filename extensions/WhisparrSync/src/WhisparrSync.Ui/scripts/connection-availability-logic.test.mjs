/**
 * Behavior contract for the pure connection-availability copy logic. The runner
 * (check-connection-availability-logic.mjs) compiles connectionAvailabilityLogic.ts and passes the
 * compiled module URL via CONNECTION_AVAILABILITY_LOGIC_MODULE.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.CONNECTION_AVAILABILITY_LOGIC_MODULE);
const { notLoadedMessage, notLoadedOptionLabel } = mod;

test("first-run copy names the subject and asks the user to Test the connection", () => {
  const msg = notLoadedMessage(false, "your quality profiles");
  assert.equal(msg, "Test the connection to load your quality profiles.");
  // Subject is interpolated verbatim so each section keeps its own noun.
  assert.match(notLoadedMessage(false, "Whisparr's file settings"), /Whisparr's file settings/);
});

test("unreachable copy is subject-independent and points at retry — never implies first-time setup", () => {
  const msg = notLoadedMessage(true, "your quality profiles");
  // The distinction that fixes the reported bug: a configured-but-down connection must not read as
  // "you still have to connect". It says unreachable + retry, and drops the per-subject noun.
  assert.match(msg, /isn't reachable right now/);
  assert.match(msg, /Test connection above to retry/);
  assert.doesNotMatch(msg, /quality profiles/);
});

test("dropdown option label distinguishes the two states too", () => {
  assert.equal(notLoadedOptionLabel(false), "Test the connection to load this");
  assert.match(notLoadedOptionLabel(true), /unreachable/i);
});

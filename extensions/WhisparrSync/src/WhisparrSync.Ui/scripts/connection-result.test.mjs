/**
 * Logic-guard for the connection-result → copy mapping and the detected-version →
 * selector mapping. The runner compiles connectionResult.ts and passes the compiled module path
 * in CONNECTION_MODULE; importing the exact compiled artifact keeps the test honest about what ships. The
 * central assertion: each of the four failure classes (plus success) yields a DISTINCT message, so the UI
 * can never collapse them into one generic "failed".
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.CONNECTION_MODULE);
const { connectionCopy, selectorForDetected } = mod;

test("each result class maps to a distinct message", () => {
  const messages = [
    connectionCopy({ kind: "success", instanceName: "My Whisparr", version: "3.3.4.808" }).message,
    connectionCopy({ kind: "badKey" }).message,
    connectionCopy({ kind: "unreachable", url: "http://localhost:6969" }).message,
    connectionCopy({ kind: "notWhisparr" }).message,
    connectionCopy({ kind: "versionMismatch", detected: "2.0.2.1" }).message,
  ];

  const unique = new Set(messages);
  assert.equal(unique.size, messages.length, "every class must produce its own copy");
  for (const m of messages) {
    assert.ok(m.length > 0, "no empty message");
  }
});

test("tones match the color contract", () => {
  assert.equal(connectionCopy({ kind: "success" }).tone, "success");
  assert.equal(connectionCopy({ kind: "badKey" }).tone, "error");
  assert.equal(connectionCopy({ kind: "unreachable" }).tone, "error");
  assert.equal(connectionCopy({ kind: "notWhisparr" }).tone, "warning");
  assert.equal(connectionCopy({ kind: "versionMismatch" }).tone, "warning");
});

test("success + unreachable + mismatch copy interpolate their values", () => {
  assert.match(
    connectionCopy({ kind: "success", instanceName: "My Whisparr", version: "3.3.4.808" }).message,
    /My Whisparr.*3\.3\.4\.808/,
  );
  assert.match(
    connectionCopy({ kind: "unreachable", url: "http://localhost:6969" }).message,
    /http:\/\/localhost:6969/,
  );
  assert.match(connectionCopy({ kind: "versionMismatch", detected: "2.0.2.1" }).message, /2\.0\.2\.1/);
});

test("versionMismatch is a warning refusal, not a generic error", () => {
  const mismatch = connectionCopy({ kind: "versionMismatch", detected: "2.0.2.1" });
  assert.equal(mismatch.tone, "warning");
  assert.notEqual(mismatch.message, connectionCopy({ kind: "unreachable" }).message);
  assert.notEqual(mismatch.message, connectionCopy({ kind: "badKey" }).message);
});

test("selectorForDetected maps a version to its selector, null when unparseable", () => {
  assert.equal(selectorForDetected("3.3.4.808"), "v3");
  assert.equal(selectorForDetected("2.0.2.1"), "v2");
  assert.equal(selectorForDetected("eros"), null);
  assert.equal(selectorForDetected(""), null);
  assert.equal(selectorForDetected(null), null);
  assert.equal(selectorForDetected(undefined), null);
});

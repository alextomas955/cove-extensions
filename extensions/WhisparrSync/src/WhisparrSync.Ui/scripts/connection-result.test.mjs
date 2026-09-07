/**
 * Logic-guard for the connection-result → copy mapping, the detected-version →
 * selector mapping, and the address a reading belongs to. The runner compiles connectionResult.ts and passes
 * the compiled module path in CONNECTION_MODULE; importing the exact compiled artifact keeps the test honest
 * about what ships. The central assertion: each of the four failure classes (plus success) yields a DISTINCT
 * message, so the UI can never collapse them into one generic "failed".
 *
 * The address cases assert WHOLE sentences, never containment. The defect they exist for is a sentence that
 * is entirely TRUE of the instance it was read from and false of the one on screen, so every substring of the
 * stale banner is present in the correct one too and a containment check passes on the broken output.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.CONNECTION_MODULE);
const { connectionCopy, readingForAddress, selectorForDetected } = mod;

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

// A real address, an unreachable repoint of it, and the exact sentence the panel renders for that
// instance. The sentence is a literal rather than a value built from the module under test, so a copy
// change has to be made here deliberately instead of passing by construction.
const STORED = "http://host.docker.internal:6972";
const REPOINTED = "http://localhost:6972";
const CONNECTED = "Connected to Whisparr — Whisparr 2.2.0.108.";

const reading = (over = {}) => ({
  kind: "success",
  url: STORED,
  instanceName: "Whisparr",
  version: "2.2.0.108",
  ...over,
});

test("a reading shows while the field still holds the address it was taken against", () => {
  assert.equal(connectionCopy(readingForAddress(reading(), STORED)).message, CONNECTED);
});

test("a reading is dropped once the field holds a different host", () => {
  assert.equal(readingForAddress(reading(), REPOINTED), null);
});

test("every class is dropped by the same rule — success is not the only sentence that goes stale", () => {
  for (const kind of ["success", "badKey", "unreachable", "notWhisparr", "versionMismatch"]) {
    assert.equal(
      readingForAddress(reading({ kind }), REPOINTED),
      null,
      `${kind} survived an address change`,
    );
  }
});

test("the dropped reading is retired, never reworded into a different sentence", () => {
  // The banner reports a test that genuinely happened, so the only honest alternatives are the same
  // sentence or no sentence. A non-null answer here carrying different copy would be an invented result.
  const shown = readingForAddress(reading(), REPOINTED);
  assert.equal(shown, null);
  assert.equal(connectionCopy(reading()).message, CONNECTED);
});

test("a cosmetic edit keeps the reading — the host rule is the server's own trimmed, case-insensitive one", () => {
  assert.equal(connectionCopy(readingForAddress(reading(), `${STORED}/`)).message, CONNECTED);
  assert.equal(connectionCopy(readingForAddress(reading(), `${STORED}///`)).message, CONNECTED);
  assert.equal(
    connectionCopy(readingForAddress(reading(), "HTTP://HOST.DOCKER.INTERNAL:6972")).message,
    CONNECTED,
  );
  assert.equal(
    connectionCopy(readingForAddress(reading({ url: `${STORED}/` }), STORED)).message,
    CONNECTED,
  );
});

test("typing the address back shows the same reading again — a true statement is never destroyed", () => {
  const held = reading();
  assert.equal(readingForAddress(held, REPOINTED), null);
  assert.equal(connectionCopy(readingForAddress(held, STORED)).message, CONNECTED);
});

test("no reading, no sentence", () => {
  assert.equal(readingForAddress(null, STORED), null);
  assert.equal(readingForAddress(reading(), ""), null);
});

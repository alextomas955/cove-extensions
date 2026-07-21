/**
 * Logic-guard for the action-failure classification + message mapping. The runner compiles
 * actionFailureLogic.ts and passes the compiled module path in ACTION_FAILURE_MODULE; importing the
 * exact compiled artifact keeps the test honest about what ships. The central assertion: the rendered
 * copy never leaks the raw status code or the raw JSON discriminator fields.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.ACTION_FAILURE_MODULE);
const { classifyActionFailure, actionFailureMessage, actionFailureCopy, rejectedMessage } = mod;

const ALL_KINDS = ["badKey", "notWhisparr", "unreachable", "rejected", "noIdentity", "versionUnsupported", "unknown"];

test("classifyActionFailure: 502 discriminates badKey / notWhisparr / rejected / unreachable (catch-all)", () => {
  assert.equal(classifyActionFailure(502, '{"result":"badKey"}'), "badKey");
  assert.equal(classifyActionFailure(502, '{"result":"notWhisparr"}'), "notWhisparr");
  assert.equal(classifyActionFailure(502, '{"result":"rejected","message":"nope"}'), "rejected");
  assert.equal(classifyActionFailure(502, '{"result":"unreachable"}'), "unreachable");
  assert.equal(classifyActionFailure(502, '{"result":"somethingElse"}'), "unreachable");
  assert.equal(classifyActionFailure(502, null), "unreachable");
});

test("rejected: surfaces Whisparr's own message, and degrades to a generic sentence when absent", () => {
  assert.equal(rejectedMessage('{"result":"rejected","message":"Failed to connect to qBittorrent."}'), "Failed to connect to qBittorrent.");
  assert.equal(rejectedMessage('{"result":"rejected"}'), null);
  assert.equal(rejectedMessage("not json"), null);
  assert.equal(
    actionFailureMessage("rejected", "cap-copy", "Failed to connect to qBittorrent."),
    "Whisparr couldn't complete this: Failed to connect to qBittorrent.",
  );
  assert.equal(actionFailureMessage("rejected", "cap-copy", null), "Whisparr couldn't complete this action.");
  // The full copy surfaces Whisparr's real reason instead of a generic "can't reach Whisparr".
  assert.equal(
    actionFailureCopy("grab this release", 502, '{"result":"rejected","message":"No matching movie"}', "cap-copy"),
    "Couldn't grab this release — Whisparr couldn't complete this: No matching movie",
  );
});

test("classifyActionFailure: 400 discriminates noIdentity / versionUnsupported / unknown", () => {
  assert.equal(classifyActionFailure(400, '{"code":"NO_STASHDB_IDENTITY"}'), "noIdentity");
  assert.equal(
    classifyActionFailure(400, '{"code":"VERSION_UNSUPPORTED","detected":"2.0.2.1"}'),
    "versionUnsupported",
  );
  assert.equal(classifyActionFailure(400, '{"code":"UNKNOWN_KIND"}'), "unknown");
  assert.equal(classifyActionFailure(400, "not json"), "unknown");
});

test("classifyActionFailure: any other status is unknown", () => {
  assert.equal(classifyActionFailure(403, null), "unknown");
  assert.equal(classifyActionFailure(500, "{}"), "unknown");
  assert.equal(classifyActionFailure(-1, null), "unknown");
});

test("actionFailureMessage: every kind yields a non-empty, mutually distinct message (except versionUnsupported returns its argument verbatim)", () => {
  const messages = ALL_KINDS.map((kind) => actionFailureMessage(kind, "cap-copy"));
  for (const m of messages) {
    assert.equal(typeof m, "string");
    assert.ok(m.length > 0, "no empty message");
  }
  const nonCapabilityMessages = messages.filter((_m, i) => ALL_KINDS[i] !== "versionUnsupported");
  assert.equal(
    new Set(nonCapabilityMessages).size,
    nonCapabilityMessages.length,
    "every non-versionUnsupported kind must produce its own distinct message",
  );
  assert.equal(actionFailureMessage("versionUnsupported", "cap-copy"), "cap-copy");
});

test("actionFailureCopy: exact wording for a known kind", () => {
  assert.equal(
    actionFailureCopy("update monitoring", 502, '{"result":"badKey"}', "cap-copy"),
    "Couldn't update monitoring — Whisparr rejected the saved API key. Check it in Settings.",
  );
});

test("actionFailureCopy: the rendered copy never leaks the raw status or the raw JSON discriminator fields", () => {
  for (const kind of ALL_KINDS) {
    const [status, body] =
      kind === "noIdentity"
        ? [400, '{"code":"NO_STASHDB_IDENTITY"}']
        : kind === "versionUnsupported"
          ? [400, '{"code":"VERSION_UNSUPPORTED","detected":"2.0.2.1"}']
          : kind === "unknown"
            ? [400, '{"code":"UNKNOWN_KIND"}']
            : [502, `{"result":"${kind}"}`];
    const copy = actionFailureCopy("do the thing", status, body, "cap-copy");
    assert.equal(copy.includes("502"), false, `${kind}: must not leak 502`);
    assert.equal(copy.includes("400"), false, `${kind}: must not leak 400`);
    assert.equal(copy.includes('"result"'), false, `${kind}: must not leak the raw "result" field`);
    assert.equal(copy.includes('"code"'), false, `${kind}: must not leak the raw "code" field`);
  }
});

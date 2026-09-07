/**
 * Behavior contract for the pure configuration-guard wording. The runner compiles configGuardLogic.ts and
 * passes the compiled module path in CONFIG_GUARD_LOGIC_MODULE; importing the exact compiled artifact keeps the
 * test honest about what ships.
 *
 * Each sentence is asserted as a VERBATIM literal, not by shape or substring: these are the only durable tie
 * between the shipped copy and the outcome it promises, so a later edit that drifts one must fail here.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.CONFIG_GUARD_LOGIC_MODULE);
const { configOptionLabel, configGuardMessage, configShortReason, configIncompleteCopyFrom } = mod;
// Compiled into the same scratch dir by this gate's module closure, so the settings save bar's WHOLE
// rendered line can be asserted here rather than only the wording half of it.
const { actionFailureCopy } = await import(
  new URL("./actionFailureLogic.js", process.env.CONFIG_GUARD_LOGIC_MODULE).href
);

const ADDRESS_SENTENCE =
  "Set the Whisparr URL in Whisparr Sync settings (Connection) before acting — without it, every Whisparr action fails as though Whisparr were not running.";

const KEY_SENTENCE =
  "Set the API key in Whisparr Sync settings (Connection) before acting — without it, Whisparr rejects every request.";

test("configOptionLabel: each key maps to the label the settings UI actually shows", () => {
  assert.equal(configOptionLabel("baseUrl"), "Whisparr URL");
  assert.equal(configOptionLabel("apiKey"), "API key");
});

test("configOptionLabel: an unrecognised key falls back and never echoes the key itself", () => {
  const label = configOptionLabel("someFutureOption");
  assert.equal(label.length > 0, true);
  assert.equal(label.includes("someFutureOption"), false);
});

test("each single unmet option names its setting, its settings section, and its consequence", () => {
  assert.equal(configGuardMessage(["baseUrl"]), ADDRESS_SENTENCE);
  assert.equal(configGuardMessage(["apiKey"]), KEY_SENTENCE);
  for (const key of ["baseUrl", "apiKey"]) {
    const msg = configGuardMessage([key]);
    assert.equal(msg.includes("settings"), true, `${key} must send the user to settings`);
    assert.equal(msg.includes(" — "), true, `${key} must state a consequence`);
  }
});

test("two unmet options yield ONE sentence naming both — never a composite 'one or more' phrasing", () => {
  const msg = configGuardMessage(["baseUrl", "apiKey"]);
  assert.equal(msg.includes("Whisparr URL"), true);
  assert.equal(msg.includes("API key"), true);
  assert.equal(msg.includes("Connection"), true);
  for (const vague of ["one or more", "some settings", "a setting is"]) {
    assert.equal(msg.includes(vague), false, `must not hedge with "${vague}"`);
  }
});

test("an unrecognised key falls back instead of emitting a sentence with a gap in it", () => {
  for (const keys of [[], ["someFutureOption"]]) {
    const msg = configGuardMessage(keys);
    assert.equal(msg.length > 0, true);
    assert.equal(msg.includes("undefined"), false);
    assert.equal(msg.includes("()"), false);
    assert.equal(msg.includes("someFutureOption"), false);
  }
});

test("the wording is a guided fix — it never blames the user and never nudges a version migration", () => {
  for (const key of ["baseUrl", "apiKey"]) {
    const msg = configGuardMessage([key]).toLowerCase();
    for (const banned of ["you forgot", "invalid", "migrate", "upgrade whisparr"]) {
      assert.equal(msg.includes(banned), false, `${key} must not say "${banned}"`);
    }
  }
});

test("one unmet option yields a short reason naming that setting and its section, and nothing else", () => {
  assert.equal(configShortReason(["baseUrl"]), "Needs the Whisparr URL setting (Connection)");
  assert.equal(configShortReason(["apiKey"]), "Needs the API key setting (Connection)");
});

test("several unmet options yield ONE short reason naming every one of them", () => {
  const all = configShortReason(["baseUrl", "apiKey"]);
  assert.equal(all, "Needs the Whisparr URL (Connection) and API key (Connection) settings");
  for (const vague of ["one or more", "some settings", "a setting is"]) {
    assert.equal(all.includes(vague), false, `must not hedge with "${vague}"`);
  }
});

test("an unknown key set yields a neutral short reason that names no wire key", () => {
  for (const keys of [[], ["someFutureOption"]]) {
    const reason = configShortReason(keys);
    assert.equal(reason, "Needs a Whisparr Sync setting that isn't usable yet");
    assert.equal(reason.includes("someFutureOption"), false);
    assert.equal(reason.includes("undefined"), false);
  }
});

test("short and long name the SAME setting and the SAME section for the same key set", () => {
  for (const [key, label, section] of [
    ["baseUrl", "whisparr url", "connection"],
    ["apiKey", "api key", "connection"],
  ]) {
    const long = configGuardMessage([key]).toLowerCase();
    const short = configShortReason([key]).toLowerCase();
    assert.equal(long.includes(label) && short.includes(label), true, `${key}: label drift`);
    assert.equal(long.includes(section) && short.includes(section), true, `${key}: section drift`);
  }
});

test("the short reason cannot be counted as a copy of the full sentence", () => {
  // A live DOM count of the full sentence keys on its opening literal. A short reason containing that opening
  // would read as one more copy of the very sentence it exists to avoid repeating.
  for (const keys of [["baseUrl"], ["baseUrl", "apiKey"], []]) {
    assert.equal(configShortReason(keys).includes("Set the Whisparr URL in Whisparr Sync"), false);
  }
});

test("the short reason is a guided fix too — it never blames the user or nudges a version migration", () => {
  for (const key of ["baseUrl", "apiKey"]) {
    const reason = configShortReason([key]).toLowerCase();
    for (const banned of ["you forgot", "invalid", "migrate", "upgrade whisparr"]) {
      assert.equal(reason.includes(banned), false, `${key} must not say "${banned}"`);
    }
  }
});

test("configIncompleteCopyFrom: an EMPTY body still yields a setting-shaped sentence, never a raw body", () => {
  // The shape the options route actually produces on a bind failure: a 400 with no body at all.
  assert.equal(configIncompleteCopyFrom(null), configGuardMessage([]));
  assert.equal(configIncompleteCopyFrom(""), configGuardMessage([]));
  assert.equal(configIncompleteCopyFrom("not json at all"), configGuardMessage([]));
});

test("configIncompleteCopyFrom: a CONFIG_INCOMPLETE refusal names the setting the server named", () => {
  const body = JSON.stringify({ code: "CONFIG_INCOMPLETE", options: ["baseUrl"] });
  assert.equal(configIncompleteCopyFrom(body), ADDRESS_SENTENCE);
});

test("the rendered save line leaks neither the extension id nor a transport status", () => {
  // The bodyless-400 case is what shipped an internal route path and an untranslated HTTP status to
  // any user who could reach Settings. Both negatives are paired with the equality assertion below —
  // a wrong-but-clean sentence would satisfy a negative on its own.
  const line = actionFailureCopy("save", 400, null, "unused", configIncompleteCopyFrom(null));
  assert.equal(line, `Couldn't save — Something went wrong. Try again in a moment.`);
  assert.equal(line.includes("com.alextomas955.whisparrsync"), false);
  assert.equal(line.includes("API 400"), false);
  assert.equal(line.includes("Bad Request"), false);
});

test("a CONFIG_INCOMPLETE save failure renders the naming sentence, still with no transport text", () => {
  const body = JSON.stringify({ code: "CONFIG_INCOMPLETE", options: ["baseUrl"] });
  const line = actionFailureCopy("save", 400, body, "unused", configIncompleteCopyFrom(body));
  assert.equal(line, `Couldn't save — ${ADDRESS_SENTENCE}`);
  assert.equal(line.includes("com.alextomas955.whisparrsync"), false);
  assert.equal(line.includes("API 400"), false);
});

test("a configuration refusal composes the whole line the Missing tab's per-card Monitor renders", () => {
  // The label is the discovery slice's own per-card verb phrase, pinned here because the live driver splits the
  // rendered line at this composition's separator and compares the remainder by equality.
  const body = JSON.stringify({ code: "CONFIG_INCOMPLETE", options: ["baseUrl"] });
  const line = actionFailureCopy(
    "mark this scene wanted",
    400,
    body,
    "unused",
    configIncompleteCopyFrom(body),
  );
  assert.equal(line, `Couldn't mark this scene wanted — ${ADDRESS_SENTENCE}`);
  assert.equal(line.includes("baseUrl"), false);
  assert.equal(line.includes("CONFIG_INCOMPLETE"), false);
});

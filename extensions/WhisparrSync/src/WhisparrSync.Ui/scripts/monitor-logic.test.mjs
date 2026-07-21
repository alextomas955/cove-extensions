/**
 * Behavior contract for the pure monitor logic. The runner compiles monitorLogic.ts and passes the compiled
 * module path in MONITOR_LOGIC_MODULE; importing the exact compiled artifact keeps the test honest about
 * what ships.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.MONITOR_LOGIC_MODULE);
const {
  MONITORED_LABEL,
  VERSION_CAPABILITY_COPY,
  WHISPARR_UNAVAILABLE_COPY,
  MONITOR_SCOPE_OPTIONS,
  DEFAULT_MONITOR_SCOPE,
  MONITOR_SCOPE_HEADING,
  MONITOR_SCOPE_HELP,
  statusLineText,
  shouldShowStatusLine,
  hasCounts,
  entityKindOf,
  entityOf,
  monitorRequestBody,
  statusRequestBody,
  monitorButtonTitle,
} = mod;

test("statusLineText frames present-in-library over catalogue, not a monitored-scenes count", () => {
  assert.equal(statusLineText(12, 40), "Monitored in Whisparr · 12 of 40 in library");
  assert.equal(statusLineText(5, 18), "Monitored in Whisparr · 5 of 18 in library");
  assert.equal(statusLineText(3, 390), "Monitored in Whisparr · 3 of 390 in library");
  assert.equal(statusLineText(0, 3), "Monitored in Whisparr · 0 of 3 in library");
  // The bare label is a distinct constant the caller uses when counts are absent.
  assert.equal(MONITORED_LABEL, "Monitored in Whisparr");
  // The count fragment (everything after the middot) is the present-over-catalogue number: it must say
  // "in library" and must NOT call the number a monitored count — 3 = present-in-library, 390 = catalogue.
  const fragment = statusLineText(3, 390).split(" · ")[1];
  assert.equal(fragment, "3 of 390 in library");
  assert.equal(fragment.toLowerCase().includes("monitored"), false);
});

test("VERSION_CAPABILITY_COPY is exactly the approved wording and implies no migration", () => {
  assert.equal(VERSION_CAPABILITY_COPY, "Currently available on Whisparr v3 (Eros)");
  // The whole point of the wording: v2 and v3 are both first-class — it must never nudge the user to migrate.
  for (const verb of ["upgrade", "switch", "migrate", "need", "needs", "must", "require"]) {
    assert.equal(
      VERSION_CAPABILITY_COPY.toLowerCase().includes(verb),
      false,
      `copy must not contain the migration verb "${verb}"`,
    );
  }
});

test("monitorButtonTitle: unsupported reads the version-capability copy; every other outcome is unchanged", () => {
  const base = {
    kind: "studio",
    loading: false,
    noIdentity: false,
    unsupported: false,
    error: false,
    monitored: false,
  };
  assert.equal(monitorButtonTitle({ ...base, unsupported: true }), VERSION_CAPABILITY_COPY);
  assert.equal(
    monitorButtonTitle({ ...base, kind: "performer", unsupported: true }),
    VERSION_CAPABILITY_COPY,
  );
  assert.equal(monitorButtonTitle({ ...base, kind: null }), "Whisparr");
  assert.equal(monitorButtonTitle({ ...base, loading: true }), "Checking Whisparr…");
  assert.equal(
    monitorButtonTitle({ ...base, noIdentity: true }),
    "No metadata link Whisparr can use for this studio.",
  );
  // The noIdentity copy is version-neutral: it must not name a specific metadata server (v2 uses ThePornDB).
  assert.equal(
    monitorButtonTitle({ ...base, kind: "performer", noIdentity: true }).includes("StashDB"),
    false,
  );
  assert.equal(monitorButtonTitle({ ...base, error: true }), WHISPARR_UNAVAILABLE_COPY);
  assert.equal(
    monitorButtonTitle({ ...base, monitored: true }),
    "Monitored in Whisparr — open the menu",
  );
  assert.equal(monitorButtonTitle(base), "Open the Whisparr menu for this studio");
  assert.equal(
    monitorButtonTitle({ ...base, kind: "performer" }),
    "Open the Whisparr menu for this performer",
  );
});

test("shouldShowStatusLine: true only when monitored; false when not monitored / null / undefined", () => {
  assert.equal(shouldShowStatusLine({ added: true, monitored: true, scenesPresent: 1, scenesTotal: 2 }), true);
  assert.equal(
    shouldShowStatusLine({ added: true, monitored: false, scenesPresent: 0, scenesTotal: 0 }),
    false,
  );
  assert.equal(shouldShowStatusLine(null), false);
  assert.equal(shouldShowStatusLine(undefined), false);
});

test("hasCounts honors the server flag, else derives from scenesTotal > 0; null → false", () => {
  assert.equal(hasCounts({ added: true, monitored: true, scenesPresent: 3, scenesTotal: 10, hasCounts: true }), true);
  assert.equal(
    hasCounts({ added: true, monitored: true, scenesPresent: 0, scenesTotal: 0, hasCounts: false }),
    false,
  );
  // No explicit flag: derive from scenesTotal.
  assert.equal(hasCounts({ added: true, monitored: true, scenesPresent: 1, scenesTotal: 4 }), true);
  assert.equal(hasCounts({ added: true, monitored: true, scenesPresent: 0, scenesTotal: 0 }), false);
  assert.equal(hasCounts(null), false);
});

test("entityKindOf: studio when props.studio present, performer when props.performer present, else null", () => {
  assert.equal(entityKindOf({ studio: { remoteIds: [] } }), "studio");
  assert.equal(entityKindOf({ performer: { remoteIds: [] } }), "performer");
  assert.equal(entityKindOf({}), null);
  // studio wins if (pathologically) both are present — deterministic, never a crash.
  assert.equal(entityKindOf({ studio: { remoteIds: [] }, performer: { remoteIds: [] } }), "studio");
});

test("entityOf returns the present entity object (studio or performer), else null", () => {
  const studio = { remoteIds: [{ endpoint: "https://stashdb.org/graphql", remoteId: "s1" }] };
  const performer = { remoteIds: [{ endpoint: "https://stashdb.org/graphql", remoteId: "p1" }] };
  assert.equal(entityOf({ studio }), studio);
  assert.equal(entityOf({ performer }), performer);
  assert.equal(entityOf({}), null);
});

test("monitorRequestBody shapes { Kind, RemoteIds:[{Endpoint,RemoteId}], Monitored } (PascalCase wire)", () => {
  const remoteIds = [
    { endpoint: "https://stashdb.org/graphql", remoteId: "abc" },
    { endpoint: "https://theporndb.net/graphql", remoteId: "def" },
  ];
  assert.deepEqual(monitorRequestBody("studio", remoteIds, true), {
    Kind: "studio",
    RemoteIds: [
      { Endpoint: "https://stashdb.org/graphql", RemoteId: "abc" },
      { Endpoint: "https://theporndb.net/graphql", RemoteId: "def" },
    ],
    Monitored: true,
  });
  assert.deepEqual(monitorRequestBody("performer", null, false), {
    Kind: "performer",
    RemoteIds: [],
    Monitored: false,
  });
  // No scope arg → the Scope key is omitted entirely (an off-toggle body stays minimal).
  assert.equal("Scope" in monitorRequestBody("studio", null, false), false);
  assert.equal("Scope" in monitorRequestBody("studio", null, true), false);
});

test("monitorRequestBody includes Scope only when the scope arg is provided", () => {
  assert.deepEqual(monitorRequestBody("studio", null, true, "AllScenes"), {
    Kind: "studio",
    RemoteIds: [],
    Monitored: true,
    Scope: "AllScenes",
  });
  assert.deepEqual(monitorRequestBody("performer", null, true, "NewReleases"), {
    Kind: "performer",
    RemoteIds: [],
    Monitored: true,
    Scope: "NewReleases",
  });
});

test("MONITOR_SCOPE_OPTIONS are the two scope choices in order; DEFAULT_MONITOR_SCOPE is NewReleases", () => {
  assert.deepEqual(
    MONITOR_SCOPE_OPTIONS.map((o) => o.value),
    ["NewReleases", "AllScenes"],
  );
  for (const opt of MONITOR_SCOPE_OPTIONS) {
    assert.equal(typeof opt.label, "string");
    assert.equal(opt.label.length > 0, true);
    assert.equal(typeof opt.description, "string");
    assert.equal(opt.description.length > 0, true);
  }
  assert.equal(DEFAULT_MONITOR_SCOPE, "NewReleases");
});

test("MONITOR_SCOPE_HEADING frames the control as the next monitor action, not a current/reflected scope", () => {
  assert.equal(typeof MONITOR_SCOPE_HEADING, "string");
  assert.equal(MONITOR_SCOPE_HEADING.length > 0, true);
  const heading = MONITOR_SCOPE_HEADING.toLowerCase();
  // Next-action framing: it speaks to what monitoring WILL do.
  assert.equal(heading.includes("monitor"), true);
  // It must NOT assert a current/reflected Whisparr scope (Whisparr stores none).
  for (const stateWord of ["current", "currently", "now monitored", "monitored scope"]) {
    assert.equal(
      heading.includes(stateWord),
      false,
      `heading must not claim a current scope via "${stateWord}"`,
    );
  }
});

test("MONITOR_SCOPE_HELP is next-action framed and carries the all-scenes back-catalogue escalation", () => {
  assert.equal(typeof MONITOR_SCOPE_HELP, "string");
  assert.equal(MONITOR_SCOPE_HELP.length > 0, true);
  const help = MONITOR_SCOPE_HELP.toLowerCase();
  // (a) frames the radio as choosing what the next Monitor does.
  assert.equal(help.includes("next monitor"), true);
  // (b) WSYNC-30-3 escalation: "All scenes" also queues the existing back-catalogue.
  assert.equal(help.includes("back-catalogue"), true);
  // Switching back to New releases only leaves already-monitored scenes as they are.
  assert.equal(help.includes("new releases only"), true);
  assert.equal(help.includes("already monitored"), true);
  // It must not claim a current/reflected scope either.
  assert.equal(help.includes("current scope"), false);
});

test("statusRequestBody shapes { Kind, RemoteIds:[{Endpoint,RemoteId}] } and carries no url/key", () => {
  const remoteIds = [{ endpoint: "https://stashdb.org/graphql", remoteId: "xyz" }];
  const body = statusRequestBody("performer", remoteIds);
  assert.deepEqual(body, {
    Kind: "performer",
    RemoteIds: [{ Endpoint: "https://stashdb.org/graphql", RemoteId: "xyz" }],
  });
  // No credential fields ever leak into the body.
  assert.equal("BaseUrl" in body, false);
  assert.equal("ApiKey" in body, false);
  assert.deepEqual(statusRequestBody("studio", undefined), { Kind: "studio", RemoteIds: [] });
});

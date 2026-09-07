/**
 * Behavior contract for the pure pipeline-health logic. The gate runner compiles pipelineHealthLogic.ts and
 * passes the compiled module URL via PIPELINE_HEALTH_LOGIC_MODULE.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.PIPELINE_HEALTH_LOGIC_MODULE);
const {
  pipelineHealthFromServer,
  acquisitionLastHealthyTicks,
  connectionTimes,
  ACQUISITION_DEPENDENCY,
  NO_PIPELINE_HEALTH,
  failingDependencies,
  recoveredDependencies,
  hasFailingDependency,
  hasRecoveredDependency,
  dependencyLabel,
  failingSummary,
  truncateForDisplay,
  ERROR_DISPLAY_LENGTH,
  rendersLastHealthyTime,
} = mod;

const RECENT = 638_600_000_000_000_000;
const OLD = 638_000_000_000_000_000;

const entry = (over = {}) => ({
  dependency: "acquisition",
  outcome: "ok",
  lastHealthyTicks: RECENT,
  lastFailureTicks: null,
  consecutiveFailures: 0,
  lastError: "",
  ...over,
});

test("pipelineHealthFromServer: reads the camelCase DependencyHealthView shape", () => {
  const [e] = pipelineHealthFromServer([
    {
      dependency: "acquisition",
      outcome: "unreachable",
      lastHealthyTicks: OLD,
      lastFailureTicks: RECENT,
      consecutiveFailures: 3,
      lastError: "Network is unreachable",
    },
  ]);
  assert.equal(e.dependency, "acquisition");
  assert.equal(e.outcome, "unreachable");
  assert.equal(e.lastHealthyTicks, OLD);
  assert.equal(e.lastFailureTicks, RECENT);
  assert.equal(e.consecutiveFailures, 3);
  assert.equal(e.lastError, "Network is unreachable");
});

test("pipelineHealthFromServer: a null tick stays null, never 0 — the epoch is not a time that happened", () => {
  const [e] = pipelineHealthFromServer([
    {
      dependency: "metadata",
      outcome: "notConfigured",
      lastHealthyTicks: null,
      lastFailureTicks: null,
    },
  ]);
  assert.equal(e.lastHealthyTicks, null);
  assert.equal(e.lastFailureTicks, null);
  assert.equal(e.consecutiveFailures, 0);
  assert.equal(e.lastError, "");
});

test("pipelineHealthFromServer: a malformed ENTRY is skipped, never bound", () => {
  const out = pipelineHealthFromServer([
    entry(),
    { outcome: "ok" }, // no dependency key
    { dependency: 7 }, // non-string dependency
    { dependency: "" }, // empty dependency
    null,
    "nope",
  ]);
  assert.equal(out.length, 1);
  assert.equal(out[0].dependency, "acquisition");
});

test("pipelineHealthFromServer: a non-array, an empty array and an absent field all render nothing", () => {
  assert.deepEqual(pipelineHealthFromServer("nope"), NO_PIPELINE_HEALTH);
  assert.deepEqual(pipelineHealthFromServer(null), NO_PIPELINE_HEALTH);
  assert.deepEqual(pipelineHealthFromServer(undefined), NO_PIPELINE_HEALTH);
  assert.deepEqual(pipelineHealthFromServer({ dependency: "acquisition" }), NO_PIPELINE_HEALTH);
  assert.deepEqual(pipelineHealthFromServer([]), NO_PIPELINE_HEALTH);
});

test("pipelineHealthFromServer: wrong-typed scalars fall back rather than throwing", () => {
  const [e] = pipelineHealthFromServer([
    {
      dependency: "import",
      outcome: 42,
      lastHealthyTicks: "soon",
      lastFailureTicks: Number.NaN,
      consecutiveFailures: "three",
      lastError: { evil: true },
    },
  ]);
  assert.equal(e.outcome, "");
  assert.equal(e.lastHealthyTicks, null);
  assert.equal(e.lastFailureTicks, null);
  assert.equal(e.consecutiveFailures, 0);
  assert.equal(e.lastError, "");
});

test("acquisitionLastHealthyTicks: selects the acquisition row, null when it has never been healthy", () => {
  assert.equal(acquisitionLastHealthyTicks([entry()]), RECENT);
  assert.equal(acquisitionLastHealthyTicks([entry({ lastHealthyTicks: null })]), null);
  assert.equal(acquisitionLastHealthyTicks([entry({ dependency: "metadata" })]), null);
  assert.equal(acquisitionLastHealthyTicks([]), null);
  assert.equal(ACQUISITION_DEPENDENCY, "acquisition");
});

test("rendersLastHealthyTime: only acquisition is suppressed, and an unknown key keeps its time", () => {
  // Each key asserted by name rather than in a loop: renaming one must fail here, not silently pass.
  assert.equal(rendersLastHealthyTime("acquisition"), false);
  assert.equal(rendersLastHealthyTime(ACQUISITION_DEPENDENCY), false);
  assert.equal(rendersLastHealthyTime("metadata"), true);
  assert.equal(rendersLastHealthyTime("import"), true);
  // An unknown dependency has no other home for its time, so the fallback shows it rather than losing it.
  assert.equal(rendersLastHealthyTime("something-new"), true);
  assert.equal(rendersLastHealthyTime(""), true);
});

test("connectionTimes: the two ticks are INDEPENDENT — neither moves when only the other's input moves", () => {
  // The regression this case exists to catch: wiring the acquisition tick into the version line. The inputs
  // deliberately DISAGREE — a freshly healthy acquisition row beside an old verification tick.
  const fresh = connectionTimes(OLD, [entry({ lastHealthyTicks: RECENT })]);
  assert.equal(
    fresh.versionVerifiedTicks,
    OLD,
    "the version's tick must not borrow the reachability tick",
  );
  assert.equal(fresh.whisparrLastReachableTicks, RECENT);

  // Move ONLY the health input: the version's tick must not follow.
  const healthMoved = connectionTimes(OLD, [entry({ lastHealthyTicks: RECENT + 1 })]);
  assert.equal(healthMoved.versionVerifiedTicks, OLD);
  assert.equal(healthMoved.whisparrLastReachableTicks, RECENT + 1);

  // Move ONLY the version input: the reachability tick must not follow.
  const versionMoved = connectionTimes(RECENT, [entry({ lastHealthyTicks: RECENT + 1 })]);
  assert.equal(versionMoved.versionVerifiedTicks, RECENT);
  assert.equal(versionMoved.whisparrLastReachableTicks, RECENT + 1);
});

test("connectionTimes: an absent verification tick stays absent even while Whisparr is reachable now", () => {
  const t = connectionTimes(null, [entry({ lastHealthyTicks: RECENT })]);
  assert.equal(t.versionVerifiedTicks, null);
  assert.equal(t.whisparrLastReachableTicks, RECENT);
});

test("connectionTimes: a verified version with no health record reports no reachability time", () => {
  const t = connectionTimes(RECENT, []);
  assert.equal(t.versionVerifiedTicks, RECENT);
  assert.equal(t.whisparrLastReachableTicks, null);
});

const failing = (over = {}) =>
  entry({
    outcome: "unreachable",
    lastHealthyTicks: OLD,
    lastFailureTicks: RECENT,
    consecutiveFailures: 3,
    lastError: "Network is unreachable (host.docker.internal:6999)",
    ...over,
  });

const recovered = (over = {}) =>
  entry({
    outcome: "ok",
    lastHealthyTicks: RECENT,
    lastFailureTicks: OLD,
    consecutiveFailures: 0,
    lastError: "Whisparr rejected the stored API key.",
    ...over,
  });

test("a non-zero consecutive count classifies as FAILING and is returned by the failing selector", () => {
  const entries = [failing()];
  assert.equal(hasFailingDependency(entries), true);
  assert.equal(hasRecoveredDependency(entries), false);
  assert.deepEqual(failingDependencies(entries), entries);
  assert.deepEqual(recoveredDependencies(entries), []);
});

test("a zero count with a retained error classifies as RECOVERED, and never as failing", () => {
  // The error survives recovery, and must NOT raise an alert.
  const entries = [recovered()];
  assert.equal(hasRecoveredDependency(entries), true);
  assert.equal(hasFailingDependency(entries), false);
  assert.deepEqual(recoveredDependencies(entries), entries);
  assert.deepEqual(failingDependencies(entries), []);
});

test("a zero count with an empty error is neither — a pipeline that never failed renders nothing", () => {
  const entries = [entry()];
  assert.equal(hasFailingDependency(entries), false);
  assert.equal(hasRecoveredDependency(entries), false);
});

test("an empty, non-array or malformed projection classifies as nothing to render and never throws", () => {
  for (const raw of [[], "nope", null, undefined, [{ nope: 1 }, null, "x"]]) {
    const entries = pipelineHealthFromServer(raw);
    assert.equal(hasFailingDependency(entries), false);
    assert.equal(hasRecoveredDependency(entries), false);
  }
});

test("dependencyLabel: the three keys are stable, and an unknown key falls back to the key itself", () => {
  assert.equal(dependencyLabel("acquisition"), "Whisparr");
  assert.equal(dependencyLabel("metadata"), "Metadata provider");
  assert.equal(dependencyLabel("import"), "Import channel");
  // A blank label would make an unknown row invisible.
  assert.equal(dependencyLabel("somethingNew"), "somethingNew");
  assert.notEqual(dependencyLabel("somethingNew"), "");
});

test("failingSummary: names the outcome and the count, and does NOT embed the error text", () => {
  const e = failing();
  const s = failingSummary(e);
  assert.ok(s.includes("unreachable"), s);
  assert.ok(s.includes("3"), s);
  assert.ok(s.includes("Whisparr"), s);
  // The retained provider string is rendered separately as a text child; it must never reach a string builder.
  assert.ok(!s.includes(e.lastError), s);
  assert.ok(!s.includes("host.docker.internal"), s);
});

test("failingSummary: one failure is singular", () => {
  assert.ok(failingSummary(failing({ consecutiveFailures: 1 })).includes("1 consecutive failure."));
  assert.ok(
    failingSummary(failing({ consecutiveFailures: 2 })).includes("2 consecutive failures."),
  );
});

test("truncateForDisplay: bounds LAYOUT independently of the 512-character write cap", () => {
  const short = "Connection refused";
  assert.equal(truncateForDisplay(short), short);
  const long = "x".repeat(512);
  const out = truncateForDisplay(long);
  assert.equal(out.length, ERROR_DISPLAY_LENGTH + 1); // the ellipsis
  assert.ok(out.endsWith("…"));
  assert.ok(ERROR_DISPLAY_LENGTH < 512, "the display cap must be tighter than the write cap");
  assert.equal(truncateForDisplay(""), "");
});

test("the failing and recovered sets partition a mixed projection without overlapping", () => {
  const entries = [
    failing({ dependency: "acquisition" }),
    recovered({ dependency: "metadata" }),
    entry({ dependency: "import", lastError: "" }),
  ];
  assert.deepEqual(
    failingDependencies(entries).map((e) => e.dependency),
    ["acquisition"],
  );
  assert.deepEqual(
    recoveredDependencies(entries).map((e) => e.dependency),
    ["metadata"],
  );
});

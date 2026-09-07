/**
 * Behavior contract for the pure activity logic. The runner compiles activityLogic.ts and passes the compiled
 * module path in ACTIVITY_LOGIC_MODULE; importing the exact compiled artifact keeps the test honest about what
 * ships. Mirrors scene-status-logic.test.mjs in shape. This is the FE side of the FE↔BE history-event casing
 * drift gate — the label/glyph/tint map MUST key on exactly the camelCase wire strings the C# enum emits
 * (grabbed/imported/failed), which ActivityProjectorTests pins on the BE side.

 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.ACTIVITY_LOGIC_MODULE);
const {
  HISTORY_EVENT_META,
  QUEUE_STATE_META,
  WANTED_META,
  clampProgress,
  showingLabel,
  activityStatus,
} = mod;
const { ACTIVITY_SECTIONS, ACTIVITY_TAB_ORDER } = mod;

test("HISTORY_EVENT_META keys on EXACTLY grabbed/imported/failed and nothing else", () => {
  assert.deepEqual(Object.keys(HISTORY_EVENT_META).sort(), ["failed", "grabbed", "imported"]);
});

test("each history event maps to its label + StatusPill variant + lucide glyph", () => {
  assert.deepEqual(HISTORY_EVENT_META.grabbed, {
    label: "Grabbed",
    iconKey: "Download",
    variant: "accent",
  });
  assert.deepEqual(HISTORY_EVENT_META.imported, {
    label: "Imported",
    iconKey: "CheckCircle2",
    variant: "green",
  });
  assert.deepEqual(HISTORY_EVENT_META.failed, {
    label: "Failed",
    iconKey: "XCircle",
    variant: "red",
  });
});

test("QUEUE_STATE_META keys on EXACTLY the five camelCase queue states and nothing stray", () => {
  assert.deepEqual(
    Object.keys(QUEUE_STATE_META).sort(),
    ["downloading", "failed", "importing", "queued", "warning"],
  );
});

test("each queue state maps to its label + StatusPill variant + lucide glyph", () => {
  assert.deepEqual(QUEUE_STATE_META.downloading, {
    label: "Downloading",
    iconKey: "Download",
    variant: "accent",
  });
  assert.deepEqual(QUEUE_STATE_META.queued, { label: "Queued", iconKey: "Clock", variant: "gray" });
  assert.deepEqual(QUEUE_STATE_META.importing, {
    label: "Importing",
    iconKey: "Download",
    variant: "green",
  });
  assert.deepEqual(QUEUE_STATE_META.warning, {
    label: "Warning",
    iconKey: "AlertTriangle",
    variant: "amber",
  });
  assert.deepEqual(QUEUE_STATE_META.failed, { label: "Failed", iconKey: "XCircle", variant: "red" });
});

test("WANTED_META is the single accent Bookmark descriptor (monitored-without-file)", () => {
  assert.deepEqual(WANTED_META, { label: "Wanted", iconKey: "Bookmark", variant: "accent" });
});

test("every meta entry (history + queue + wanted) is a glyph+label+tint triple (status never rides on color alone)", () => {
  const all = [
    ...Object.values(HISTORY_EVENT_META),
    ...Object.values(QUEUE_STATE_META),
    WANTED_META,
  ];
  for (const meta of all) {
    assert.equal(typeof meta.label, "string");
    assert.ok(meta.label.length > 0);
    assert.equal(typeof meta.iconKey, "string");
    assert.ok(meta.iconKey.length > 0);
    assert.ok(["accent", "amber", "red", "green", "gray"].includes(meta.variant));
  }
});

test("clampProgress rounds to a whole percent in [0,100]; null/undefined/NaN → undefined (indeterminate)", () => {
  assert.equal(clampProgress(0), 0);
  assert.equal(clampProgress(100), 100);
  assert.equal(clampProgress(42.6), 43);
  assert.equal(clampProgress(-5), 0);
  assert.equal(clampProgress(150), 100);
  assert.equal(clampProgress(null), undefined);
  assert.equal(clampProgress(undefined), undefined);
  assert.equal(clampProgress(NaN), undefined);
});

test("showingLabel renders 'Showing {n} of {total}'", () => {
  assert.equal(showingLabel(5, 2182), "Showing 5 of 2182");
});

test("activityStatus keeps loading / populated / empty / error as DISTINCT outcomes (outage != empty)", () => {
  const row = { scene: { sceneTitle: "x", studio: null, quality: null, date: null }, event: "imported" };
  assert.equal(activityStatus({ loading: true, error: false, rows: null }), "loading");
  // An outage (error, or a null payload) must NEVER collapse into "empty".
  assert.equal(activityStatus({ loading: false, error: true, rows: null }), "error");
  assert.equal(activityStatus({ loading: false, error: false, rows: null }), "error");
  assert.equal(activityStatus({ loading: false, error: false, rows: [] }), "empty");
  assert.equal(activityStatus({ loading: false, error: false, rows: [row] }), "populated");
});

test("ACTIVITY_SECTIONS declares the groupings in tab-bar order, keyed on the bookmarkable URL-hash literals", () => {
  assert.deepEqual(
    ACTIVITY_SECTIONS.map((s) => s.key),
    ["wanted", "queue", "history"],
  );
});

test("ACTIVITY_TAB_ORDER is DERIVED from the table, never a second literal array that can drift", () => {
  assert.deepEqual(
    [...ACTIVITY_TAB_ORDER],
    ["wanted", "queue", "history"],
  );
  assert.deepEqual(
    [...ACTIVITY_TAB_ORDER],
    ACTIVITY_SECTIONS.map((s) => s.key),
  );
});

test("every descriptor carries a label, a route and both empty-state strings", () => {
  for (const section of ACTIVITY_SECTIONS) {
    for (const field of ["key", "label", "route", "emptyHeading", "emptyBody"]) {
      assert.equal(typeof section[field], "string", `${section.key}.${field}`);
      assert.ok(section[field].length > 0, `${section.key}.${field}`);
    }
    assert.equal(typeof section.showsCount, "boolean", `${section.key}.showsCount`);
  }
});

test("the count-badge rule is DATA, not a ternary: Wanted and Queue show a count, History does not", () => {
  assert.deepEqual(
    ACTIVITY_SECTIONS.map((s) => [s.key, s.showsCount]),
    [
      ["wanted", true],
      ["queue", true],
      ["history", false],
    ],
  );
});

test("each descriptor's route is the activity path its section pages today", () => {
  assert.deepEqual(
    ACTIVITY_SECTIONS.map((s) => s.route),
    [
      "activity/wanted",
      "activity/queue",
      "activity/history",
    ],
  );
});

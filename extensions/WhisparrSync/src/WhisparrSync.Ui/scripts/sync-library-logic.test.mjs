/**
 * Behavior contract for the pure sync-library copy/body logic. The runner (check-sync-library-logic.mjs)
 * compiles syncLibraryLogic.ts and passes the compiled module URL via SYNC_LIBRARY_LOGIC_MODULE.
 */
import assert from "node:assert/strict";
import test from "node:test";

const mod = await import(process.env.SYNC_LIBRARY_LOGIC_MODULE);
const { previewSummary, syncLibraryBody, SYNC_SCOPE_OPTIONS, DEFAULT_SYNC_SCOPE } = mod;

test("syncLibraryBody is PascalCase and always carries Scope, even when not monitoring", () => {
  assert.deepEqual(syncLibraryBody({ alsoMonitor: true, scope: "AllScenes" }), {
    AlsoMonitor: true,
    Scope: "AllScenes",
  });
  // Scope is sent regardless of the toggle — the endpoint ignores it when AlsoMonitor is false, so
  // the wire shape stays stable and the C# SyncLibraryRequest never sees a missing field.
  assert.deepEqual(syncLibraryBody({ alsoMonitor: false, scope: "NewReleases" }), {
    AlsoMonitor: false,
    Scope: "NewReleases",
  });
});

test("previewSummary lists every non-empty bucket and the total skipped (v3 three-bucket)", () => {
  const counts = {
    studios: { withId: 12, skipped: 3 },
    performers: { withId: 8, skipped: 0 },
    scenes: { withId: 40, skipped: 0 },
  };
  assert.equal(
    previewSummary(counts),
    "12 studios, 8 performers, 40 scenes will sync · 3 skipped (no metadata id)",
  );
});

test("previewSummary reads studios-only when performers/scenes are empty (v2 shape)", () => {
  const summary = previewSummary({
    studios: { withId: 5, skipped: 2 },
    performers: { withId: 0, skipped: 0 },
    scenes: { withId: 0, skipped: 0 },
  });
  assert.equal(summary, "5 studios will sync · 2 skipped (no metadata id)");
  // The empty performer/scene buckets (v2 has neither) must not appear at all.
  assert.doesNotMatch(summary, /performer|scene/);
});

test("previewSummary yields a nothing-to-sync line for all-zero counts, never a throw", () => {
  const summary = previewSummary({
    studios: { withId: 0, skipped: 0 },
    performers: { withId: 0, skipped: 0 },
    scenes: { withId: 0, skipped: 0 },
  });
  assert.match(summary, /[Nn]othing to sync/);
});

test("previewSummary pluralizes on count and drops the skipped clause when zero skipped", () => {
  assert.equal(
    previewSummary({
      studios: { withId: 1, skipped: 0 },
      performers: { withId: 0, skipped: 0 },
      scenes: { withId: 0, skipped: 0 },
    }),
    "1 studio will sync",
  );
});

test("SYNC_SCOPE_OPTIONS is the ordered New/All list and the default is NewReleases", () => {
  assert.deepEqual(
    SYNC_SCOPE_OPTIONS.map((o) => o.value),
    ["NewReleases", "AllScenes"],
  );
  assert.equal(SYNC_SCOPE_OPTIONS[0].label, "New releases only");
  assert.equal(SYNC_SCOPE_OPTIONS[1].label, "All releases");
  assert.equal(DEFAULT_SYNC_SCOPE, "NewReleases");
});

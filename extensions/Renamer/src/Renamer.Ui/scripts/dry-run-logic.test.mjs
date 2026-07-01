/**
 * Behavior contract for the pure dry-run logic. The runner compiles dryRunLogic.ts and passes the
 * compiled module path in DRY_RUN_LOGIC_MODULE; importing the exact compiled artifact keeps the
 * test honest about what ships.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.DRY_RUN_LOGIC_MODULE);
const { countByStatus, paginate, totalPages } = mod;

test("countByStatus counts Renamer/Move as renamed, Skip* as skipped, NoOp as neither", () => {
  assert.deepEqual(
    countByStatus([
      { status: "Renamer" },
      { status: "Move" },
      { status: "SkipGated" },
      { status: "NoOp" },
    ]),
    { renamed: 2, skipped: 1, scanned: 4 },
  );
});

test("countByStatus on an empty array returns all zeros", () => {
  assert.deepEqual(countByStatus([]), { renamed: 0, skipped: 0, scanned: 0 });
});

test("countByStatus treats every Skip-prefixed status plus Failed as skipped", () => {
  const items = [
    { status: "SkipGated" },
    { status: "SkipCollision" },
    { status: "SkipLocked" },
    { status: "SkipBlocked" },
    { status: "SkipNoSpace" },
    { status: "SkipExcluded" },
    { status: "Failed" },
  ];
  assert.deepEqual(countByStatus(items), { renamed: 0, skipped: 7, scanned: 7 });
});

test("paginate returns the correct 50-item slice for page 0", () => {
  const items = Array.from({ length: 120 }, (_, i) => i);
  assert.deepEqual(paginate(items, 0, 50), items.slice(0, 50));
});

test("paginate returns the correct shorter final-page slice", () => {
  const items = Array.from({ length: 120 }, (_, i) => i);
  assert.deepEqual(paginate(items, 2, 50), items.slice(100, 120));
  assert.equal(paginate(items, 2, 50).length, 20);
});

test("paginate returns an empty array for a page index beyond the data", () => {
  const items = Array.from({ length: 120 }, (_, i) => i);
  assert.deepEqual(paginate(items, 5, 50), []);
});

test("totalPages never returns 0, even for an empty result", () => {
  assert.equal(totalPages(0, 50), 1);
});

test("totalPages computes the correct page count", () => {
  assert.equal(totalPages(120, 50), 3);
  assert.equal(totalPages(100, 50), 2);
});

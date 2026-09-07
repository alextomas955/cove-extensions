/**
 * Behavior contract for the pure import-log date helpers. The runner compiles importLogLogic.ts and passes
 * the compiled module path in IMPORT_LOG_LOGIC_MODULE; importing the exact compiled artifact keeps the test
 * honest about what ships.
 */
import test from "node:test";
import assert from "node:assert/strict";

const mod = await import(process.env.IMPORT_LOG_LOGIC_MODULE);
const { ticksToEpochMs, relativeTime } = mod;

test("relativeTime buckets recent/older times; ticksToEpochMs round-trips a known epoch tick", () => {
  const now = Date.parse("2026-07-13T12:00:00Z");
  assert.equal(relativeTime(now - 5_000, now), "just now");
  assert.equal(relativeTime(now - 5 * 60_000, now), "5 minutes ago");
  assert.equal(relativeTime(now - 3 * 3_600_000, now), "3 hours ago");
  assert.equal(relativeTime(now - 24 * 3_600_000, now), "yesterday");

  // 1970-01-01T00:00:00Z is 621355968000000000 .NET ticks → 0 epoch ms.
  assert.equal(ticksToEpochMs(621355968000000000), 0);
});

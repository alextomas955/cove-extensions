/**
 * Pure, DOM-free .NET-tick time helpers for the webhook section's "last event" line. Kept import-free (no
 * React, no DOM, no SDK) so the offline test runner compiles it in isolation. `relativeTime` / `ticksToEpochMs`
 * are ported from Renamer's UndoSection (the "When" column). `/import-log` returns pre-reduced
 * aggregates (a single `lastEventTicks`), so there is no longer a per-row shape to model here.
 */

// .NET DateTime.Ticks → Unix epoch ms. Ticks are 100ns since 0001-01-01; the offset is the ticks at the
// Unix epoch. Ported verbatim from Renamer's UndoSection.
const EPOCH_OFFSET_MS = 62135596800000;
const TICKS_PER_MS = 10000;
const TICKS_AT_EPOCH = EPOCH_OFFSET_MS * TICKS_PER_MS;

/** Convert a .NET tick count to Unix epoch milliseconds. */
export function ticksToEpochMs(ticks: number): number {
  return (ticks - TICKS_AT_EPOCH) / TICKS_PER_MS;
}

/** Plain relative time: "just now" / "N minutes ago" / "yesterday" / absolute beyond ~7 days. */
export function relativeTime(epochMs: number, now: number = Date.now()): string {
  const diffMs = now - epochMs;
  const sec = Math.round(diffMs / 1000);
  if (sec < 45) return "just now";
  const min = Math.round(sec / 60);
  if (min < 60) return `${min} minute${min === 1 ? "" : "s"} ago`;
  const hr = Math.round(min / 60);
  if (hr < 24) return `${hr} hour${hr === 1 ? "" : "s"} ago`;
  const day = Math.round(hr / 24);
  if (day === 1) return "yesterday";
  if (day <= 7) return `${day} days ago`;
  return new Date(epochMs).toLocaleDateString();
}

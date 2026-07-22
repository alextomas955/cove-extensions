/**
 * Pure, DOM-free helpers over the /import-log data: the row shape SettingsPage types its read on, plus the
 * relative-time helpers the webhook section's "last event" line uses. Kept import-free (no React, no DOM, no
 * SDK) so the offline test runner compiles it in isolation. `relativeTime` / `ticksToEpochMs` are ported from
 * Renamer's UndoSection (the "When" column).
 */

/**
 * One audit row as /import-log returns it (camelCase; mirrors the server ImportLogEntry). `utcTicks` is a
 * server-written .NET tick count; `result` is the locked vocabulary "Imported" | "Skipped" | "Flagged";
 * `coveEntityId` is the created/updated Cove id (present only when imported).
 */
export interface ImportLogRow {
  utcTicks: number;
  source: string; // "webhook" | "poll"
  eventType: string | null;
  path: string;
  kind: string | null; // "Video" | "Image" | "Gallery" | "Audio" | "Text" | null
  coveEntityId: number | null;
  result: string; // "Imported" | "Skipped" | "Flagged"
  reason: string | null;
  ledgerKey: string;
}

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

/**
 * The pure composition of the undo panel's one status line from the `/last-batch` summary.
 *
 * Kept import-free apart from the generated wire type (no React, no DOM, no SDK) so it stays L0 —
 * deterministic and testable with no environment — and so the sentence a user reads before a
 * destructive action is the exact sentence the suite covers. `now` is a parameter rather than a call
 * to `Date.now()` for the same reason: a clock read inside would make every expiry case untestable.
 */

import type { LastBatchSummary } from "../wire/api";

/**
 * How long the server keeps a batch before it expires, in milliseconds.
 *
 * A DELIBERATE second copy of `JournalRetention.Window` on the server. The panel states the batch's
 * actual expiry date rather than a static "kept for 7 days" note, and that date is the summary's own
 * open timestamp plus this window — computed here because the window is a constant rather than
 * per-batch data, so putting it on the wire would add a field that is the same on every response.
 *
 * The cost of that choice is this duplication, and a duplicated number with nothing watching it
 * drifts silently: the symptom would be a date the user trusts and the server does not honour. So it
 * is pinned rather than commented — `Renamer.Tests/Contracts/RetentionWindowPinTests.cs` asserts the
 * server constant is seven days and names THIS file in its failure message.
 */
export const RETENTION_WINDOW_MS = 7 * 24 * 60 * 60 * 1000;

/**
 * .NET DateTime ticks → epoch ms (ticks are 100ns since 0001-01-01).
 *
 * The tick offset between 0001-01-01 and 1970-01-01 is 621355968000000000, which exceeds
 * Number.MAX_SAFE_INTEGER (2^53). Writing it as a single literal is exact as a double but trips a
 * "literal loses precision" hint, so build it from two safe-integer factors instead: the offset in
 * milliseconds (62135596800000, well within safe range) times 10000 ticks/ms. The product is the
 * identical double value.
 */
const EPOCH_OFFSET_MS = 62135596800000;
const TICKS_PER_MS = 10000;
const TICKS_AT_EPOCH = EPOCH_OFFSET_MS * TICKS_PER_MS;

export function ticksToEpochMs(ticks: number): number {
  return (ticks - TICKS_AT_EPOCH) / TICKS_PER_MS;
}

/** What the panel needs to render and to decide whether to offer the button at all. */
export interface UndoPanelStatus {
  /** The single line stating what happened, what is left, and until when. */
  line: string;
  /** How many files the button would act on. This is the figure the confirm must quote. */
  remaining: number;
  /** Epoch ms at which the server drops this batch and everything it still holds. */
  expiresAtMs: number;
  /** True once `now` is past that moment: the batch is gone, whatever the counts say. */
  expired: boolean;
}

function plural(n: number, one: string, many: string): string {
  return n === 1 ? one : many;
}

function formatDate(epochMs: number): string {
  return new Date(epochMs).toLocaleDateString(undefined, {
    day: "numeric",
    month: "long",
    year: "numeric",
  });
}

/**
 * Build the panel's status, or `null` when there is nothing to offer.
 *
 * `null` covers both "no batch was ever journalled" and "every file in the last batch has been
 * settled" — the panel renders its "No rename to undo." branch for either, because from the user's
 * side they are the same situation.
 *
 * An EXPIRED batch is not `null`. Saying nothing there would read as "there was never a rename",
 * while the truth is that there was one and its undo window has closed; the caller uses `expired` to
 * withhold the button while still showing the line.
 */
export function buildUndoStatus(
  summary: LastBatchSummary | null | undefined,
  now: number,
): UndoPanelStatus | null {
  if (!summary?.hasBatch || summary.remainingCount <= 0) return null;

  const original = summary.count;
  const remaining = summary.remainingCount;
  const unrestorable = summary.unrestorableCount;
  // Derived, never read: the server owns the three figures that must sum, and a fourth on the wire
  // would be a fourth that can disagree with them.
  const restored = original - remaining - unrestorable;

  const expiresAtMs = ticksToEpochMs(summary.writtenAtUtcTicks) + RETENTION_WINDOW_MS;
  const expired = now >= expiresAtMs;

  const parts: string[] = [];
  if (restored > 0 || unrestorable > 0) {
    // Something has already been settled, so lead with the split. Both figures are stated so the
    // reader never subtracts one from the other to learn what is left.
    parts.push(`${restored} of ${original} restored`);
    parts.push(`${remaining} remaining`);
  } else {
    parts.push(`${original} ${plural(original, "item", "items")} renamed`);
  }
  if (unrestorable > 0) {
    parts.push(`${unrestorable} could not be restored`);
  }
  parts.push(expired ? "undo expired" : `undo available until ${formatDate(expiresAtMs)}`);

  return { line: parts.join(" · "), remaining, expiresAtMs, expired };
}

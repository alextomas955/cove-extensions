/**
 * Everything the undo footer says: its status line from `/last-batch` and its feedback from `/undo`.
 * `now` is a parameter so every expiry case is testable.
 *
 * Every number in the feedback comes from a `…Count` field, and a `…Sample` is read only to name the
 * first reason. The response describes at most a few entries per channel, so a count taken from a
 * sample's length would under-report a large undo.
 */

import type { LastBatchSummary, UndoResult } from "../wire/api";

/**
 * How long the server keeps a batch, in milliseconds: a copy of `CoveRevertJournal.RetentionWindow`,
 * which `RetentionWindowPinTests` pins to the same seven days.
 */
export const RETENTION_WINDOW_MS = 7 * 24 * 60 * 60 * 1000;

// .NET ticks are 100 ns since 0001-01-01. The epoch offset in ticks exceeds the safe-integer range as
// a literal, so it is built from two safe factors.
const EPOCH_OFFSET_MS = 62135596800000;
const TICKS_PER_MS = 10000;
const TICKS_AT_EPOCH = EPOCH_OFFSET_MS * TICKS_PER_MS;

function ticksToEpochMs(ticks: number): number {
  return (ticks - TICKS_AT_EPOCH) / TICKS_PER_MS;
}

/** What the panel needs to render and to decide whether to offer the button at all. */
interface UndoPanelStatus {
  /** The single line stating what happened, what is left, and until when. */
  line: string;
  /** How many files the button would act on. This is the figure the confirm must quote. */
  remaining: number;
  /** Epoch ms at which the batch passes out of the server's retention window. */
  expiresAtMs: number;
  /**
   * True once `now` is past that moment. The rows may still exist until the next rename purges them,
   * but the button is withheld, because the undo can no longer be promised.
   */
  expired: boolean;
}

/** What the panel renders after an undo: the status kind it styles on, and the sentence itself. */
export type UndoFeedback = { kind: "success"; text: string } | { kind: "error"; text: string };

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

/** Plain relative time: "just now" / "N minutes ago" / "yesterday" / absolute beyond ~7 days. */
function relativeTime(epochMs: number, now: number): string {
  const diffMs = now - epochMs;
  const sec = Math.round(diffMs / 1000);
  if (sec < 45) return "just now";
  const min = Math.round(sec / 60);
  if (min < 60) return `${min} ${plural(min, "minute", "minutes")} ago`;
  const hr = Math.round(min / 60);
  if (hr < 24) return `${hr} ${plural(hr, "hour", "hours")} ago`;
  const day = Math.round(hr / 24);
  if (day === 1) return "yesterday";
  if (day <= 7) return `${day} days ago`;
  return new Date(epochMs).toLocaleDateString();
}

/**
 * Build the footer's status, or `null` when no batch exists or every file in it is settled. An expired
 * batch still returns its line, with `expired` set.
 */
export function buildUndoStatus(
  summary: LastBatchSummary | null | undefined,
  now: number,
): UndoPanelStatus | null {
  if (!summary?.hasBatch || summary.remainingCount <= 0) return null;

  const original = summary.count;
  const remaining = summary.remainingCount;
  const unrestorable = summary.unrestorableCount;
  const restored = original - remaining - unrestorable;

  const writtenAtMs = ticksToEpochMs(summary.writtenAtUtcTicks);
  const expiresAtMs = writtenAtMs + RETENTION_WINDOW_MS;
  const expired = now >= expiresAtMs;

  const parts: string[] = [];
  if (restored > 0 || unrestorable > 0) {
    parts.push(`${restored} of ${original} restored`, `${remaining} remaining`);
  } else {
    parts.push(`${original} ${plural(original, "item", "items")} renamed`);
  }
  if (unrestorable > 0) {
    parts.push(`${unrestorable} could not be restored`);
  }
  parts.push(relativeTime(writtenAtMs, now));
  parts.push(expired ? "undo expired" : `undo available until ${formatDate(expiresAtMs)}`);

  return { line: parts.join(" · "), remaining, expiresAtMs, expired };
}

/**
 * Compose the sentence for a completed undo.
 *
 * Three outcomes: a clean run, a run that restored some of the batch, and a run that restored none of
 * it. A stranded companion rides on the first two rather than replacing them - the media file did come
 * back, which is what undo promises, but a slot the user owns is still occupied and nothing else will
 * say so.
 */
export function buildUndoFeedback(result: UndoResult): UndoFeedback {
  // The two problem channels are one number to a user, who cares how many files did not come back and
  // not which internal bucket held them.
  const problemCount = result.failedCount + result.skippedCount;

  const stranded =
    result.warningCount > 0
      ? ` ${result.warningCount} companion ${plural(result.warningCount, "file", "files")} stayed behind (${result.warningSample.at(0)?.detail ?? "unknown detail"}).`
      : "";

  if (problemCount === 0) {
    return {
      kind: stranded ? "error" : "success",
      text: `Undone: ${result.undone} ${plural(result.undone, "file", "files")} moved back to their original names.${stranded}`,
    };
  }

  // The one read of a sample: which reason to name. The failed channel is preferred so the order
  // matches the panel's long-standing merge. A sample is expected to be non-empty whenever its count
  // is, because the server's cap is at least one - but that is the server's promise, not something
  // this module can prove, so the empty case names itself rather than interpolating `undefined` into
  // a sentence the user reads.
  const firstReason =
    result.failedSample.at(0)?.reason ?? result.skippedSample.at(0)?.reason ?? "unknown reason";

  if (result.undone > 0) {
    return {
      kind: "error",
      text: `Undo finished with problems: ${problemCount} ${plural(problemCount, "file", "files")} couldn't be moved back (${firstReason}). The rest were restored.${stranded}`,
    };
  }

  return { kind: "error", text: `Couldn't undo: ${firstReason}. Nothing was changed.` };
}

/**
 * Compose the sentence for an undo whose response never arrived.
 *
 * `/undo` answers every arm it reaches with a body, the "nothing open to undo" arm included, so a
 * bodyless reply is raised as an `ApiError` before this one is asked for. What is left is a request
 * whose fate is unknown: the connection dropped, or the reply would not parse. The server may have
 * moved part or all of the batch back first.
 *
 * So the sentence deliberately omits "Nothing was changed", which would tell the user there is
 * nothing left to re-check, and sends them to the batch instead.
 */
export function buildUndoUnconfirmed(detail: string): UndoFeedback {
  return {
    kind: "error",
    text: `Couldn't confirm the undo: ${detail}. Some files may already have been moved back; check the batch before trying again.`,
  };
}

/** Compose the sentence for an undo the server answered with a refusal, which moved nothing. */
export function buildUndoRefused(detail: string): UndoFeedback {
  return { kind: "error", text: `Couldn't undo: ${detail}. Nothing was changed.` };
}

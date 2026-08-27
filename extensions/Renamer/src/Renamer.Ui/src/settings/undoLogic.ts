/**
 * The pure composition of everything the undo panel says: its status line from the `/last-batch`
 * summary, and its feedback sentence from the `/undo` response. One module because they are one
 * panel's copy, and they already shared a `plural` helper each had its own copy of.
 *
 * Kept import-free apart from the generated wire types (no React, no DOM, no SDK) so it stays L0 —
 * deterministic and testable with no environment — and so the sentences a user reads before and after
 * a destructive action are the exact sentences the suite covers. `now` is a parameter rather than a
 * call to `Date.now()` for the same reason: a clock read inside would make every expiry case
 * untestable.
 *
 * The feedback half is extracted from the panel for one further reason: **every number it states comes
 * from a `…Count` field, and a `…Sample` is read only to name the first reason.** The response
 * describes at most a fixed number of entries per channel because a rename batch reaches library size,
 * so a sentence built from an array's length would under-report a large undo — telling the user three
 * files could not come back when five hundred are still sitting under their renamed names. That
 * distinction is not visible in a render function and cannot be eyeballed in a review, so it lives here
 * where a test pins it. The cap's value is deliberately absent from this module: nothing here may
 * depend on how many entries the server chose to describe.
 */

import type { LastBatchSummary, UndoResult } from "../wire/api";

/**
 * How long the server keeps a batch before it expires, in milliseconds.
 *
 * A DELIBERATE second copy of `CoveRevertJournal.RetentionWindow` on the server. The panel states the
 * batch's actual expiry date rather than a static "kept for 7 days" note, and that date is the
 * summary's own open timestamp plus this window — computed here because the window is a constant
 * rather than per-batch data, so putting it on the wire would add a field that is the same on every
 * response.
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

function ticksToEpochMs(ticks: number): number {
  return (ticks - TICKS_AT_EPOCH) / TICKS_PER_MS;
}

/** What the panel needs to render and to decide whether to offer the button at all. */
export interface UndoPanelStatus {
  /** The single line stating what happened, what is left, and until when. */
  line: string;
  /** How many files the button would act on. This is the figure the confirm must quote. */
  remaining: number;
  /** Epoch ms at which the batch passes out of the server's retention window. */
  expiresAtMs: number;
  /**
   * True once `now` is past that moment.
   *
   * The rows may still be on disk: the server purges an expired batch when the NEXT batch opens and
   * nowhere else, so a library that has been quiet since keeps them. What has gone is the promise —
   * the next rename drops the batch with no further warning — so the caller withholds the button
   * rather than offering a recovery it cannot say will still be there.
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
 * Build the panel's status, or `null` when there is nothing to offer.
 *
 * `null` covers both "no batch was ever journalled" and "every file in the last batch has been
 * settled" — the panel renders its "No rename to undo." branch for either, because from the user's
 * side they are the same situation. The second test is on `remainingCount`, which the server derives
 * from the same aggregate it derives `consumed` from, so the two cannot disagree.
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

  const writtenAtMs = ticksToEpochMs(summary.writtenAtUtcTicks);
  const expiresAtMs = writtenAtMs + RETENTION_WINDOW_MS;
  const expired = now >= expiresAtMs;

  const parts: string[] = [];
  if (restored > 0 || unrestorable > 0) {
    // Something has already been settled, so lead with the split. Both figures are stated so the
    // reader never subtracts one from the other to learn what is left.
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
 * it. A stranded companion rides on the first two rather than replacing them — the media file did come
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
      text: `Undone — ${result.undone} ${plural(result.undone, "file", "files")} moved back to their original names.${stranded}`,
    };
  }

  // The one read of a sample: which reason to name. The failed channel is preferred so the order
  // matches the panel's long-standing merge. A sample is expected to be non-empty whenever its count
  // is, because the server's cap is at least one — but that is the server's promise, not something
  // this module can prove, so the empty case names itself rather than interpolating `undefined` into
  // a sentence the user reads.
  const firstReason =
    result.failedSample.at(0)?.reason ?? result.skippedSample.at(0)?.reason ?? "unknown reason";

  if (result.undone > 0) {
    return {
      kind: "error",
      text: `Undo finished with problems — ${problemCount} ${plural(problemCount, "file", "files")} couldn't be moved back (${firstReason}). The rest were restored.${stranded}`,
    };
  }

  return { kind: "error", text: `Couldn't undo — ${firstReason}. Nothing was changed.` };
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
    text: `Couldn't confirm the undo — ${detail}. Some files may already have been moved back; check the batch before trying again.`,
  };
}

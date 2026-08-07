/**
 * The "Undo last rename" settings-panel section + its destructive confirm.
 *
 * Reads GET /last-batch on mount (and whenever `refreshKey` bumps — the Review dialog's success
 * callback bumps it). Gates POST /undo behind a red destructive confirm. Feedback is honest:
 * success / partial ("k couldn't be moved back") / total failure.
 *
 * SECURITY: reasons are rendered as React text nodes (auto-escaped).
 */
import { useCallback, useEffect, useState } from "react";
import { request, requestJson, ApiError } from "@cove-extensions/ui-shared/extensionRequest";
import { Undo2 } from "lucide-react";

import { Dialog } from "../common/ui/Dialog";
import { Button, StatusText, Spinner } from "@cove-extensions/ui-shared";
import { api } from "../common/lib/extension";

const LAST_BATCH_PATH = api("last-batch");
const UNDO_PATH = api("undo");

const UNDO_TITLE_ID = "rename-undo-confirm-title";
const UNDO_DESC_ID = "rename-undo-confirm-message";

/** GET /last-batch. */
interface LastBatchSummary {
  hasBatch: boolean;
  count: number;
  writtenAtUtcTicks: number;
  consumed: boolean;
}

/** POST /undo: failed/skipped entries are { fileId, oldPath, newPath, reason }. */
interface UndoEntryError {
  fileId: number;
  oldPath: string;
  newPath: string;
  reason: string;
}
interface UndoResult {
  undone: number;
  // Optional on the wire: /undo may return a minimal/empty 200 in edge cases (see onUndo), so the
  // arrays are not guaranteed present in the response. Typing them optional keeps the defensive
  // `?.`/`?? 0` reads honest rather than asserting a shape the server doesn't promise.
  failed?: UndoEntryError[];
  skipped?: UndoEntryError[];
}

/**
 * .NET DateTime ticks → epoch ms (ticks are 100ns since 0001-01-01).
 *
 * The tick offset between 0001-01-01 and 1970-01-01 is 621355968000000000, which exceeds
 * Number.MAX_SAFE_INTEGER (2^53). Writing it as a single literal is exact as a double but trips a
 * "literal loses precision" hint, so build it from two safe-integer factors instead: the offset in
 * milliseconds (62135596800000, well within safe range) times 10000 ticks/ms. The product is the
 * identical double value — the arithmetic below is unchanged.
 */
const EPOCH_OFFSET_MS = 62135596800000;
const TICKS_PER_MS = 10000;
const TICKS_AT_EPOCH = EPOCH_OFFSET_MS * TICKS_PER_MS;
function ticksToEpochMs(ticks: number): number {
  return (ticks - TICKS_AT_EPOCH) / TICKS_PER_MS;
}

/** Plain relative time: "just now" / "N minutes ago" / "yesterday" / absolute beyond ~7 days. */
function relativeTime(epochMs: number, now: number = Date.now()): string {
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

function errText(err: unknown): string {
  return err instanceof ApiError ? `${err.status} ${err.body}` : String(err);
}

type Feedback = { kind: "success"; text: string } | { kind: "error"; text: string } | null;

export function UndoSection({ refreshKey }: { refreshKey: number }) {
  const [summary, setSummary] = useState<LastBatchSummary | null>(null);
  const [loading, setLoading] = useState(true);
  const [summaryError, setSummaryError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const [undoing, setUndoing] = useState(false);
  const [feedback, setFeedback] = useState<Feedback>(null);

  const loadSummary = useCallback(async () => {
    setLoading(true);
    setSummaryError(null);
    try {
      const res = await requestJson<LastBatchSummary>(LAST_BATCH_PATH);
      setSummary(res);
    } catch (err) {
      setSummaryError(errText(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    // Data fetch on mount / refresh: loadSummary awaits the server then setState()s the result.
    // This is the canonical "synchronize with an external system" effect, not a render-derived
    // setState — the react-compiler set-state-in-effect heuristic can't see through the async hop.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void loadSummary();
  }, [loadSummary, refreshKey]);

  // A batch is undoable only if it exists and has not been consumed.
  const hasUndoable = !!summary && summary.hasBatch && !summary.consumed;
  const count = summary?.count ?? 0;
  const writtenMs = summary ? ticksToEpochMs(summary.writtenAtUtcTicks) : 0;

  async function onUndo() {
    setUndoing(true);
    setFeedback(null);
    try {
      // /undo takes NO body. It may answer an empty 200 in edge cases, which `request` resolves as
      // undefined — a success carrying no counts to report, not a failure, so this stays on
      // `request` rather than moving to `requestJson`.
      const res = await request<UndoResult>(UNDO_PATH, { method: "POST" });
      if (!res) {
        setFeedback({
          kind: "success",
          text: "Undone — your files were moved back to their original names.",
        });
        return;
      }
      const failedCount = (res.failed?.length ?? 0) + (res.skipped?.length ?? 0);
      if (failedCount === 0) {
        setFeedback({
          kind: "success",
          text: `Undone — ${res.undone} file${res.undone === 1 ? "" : "s"} moved back to their original names.`,
        });
      } else if (res.undone > 0) {
        const reason = res.failed?.[0]?.reason ?? res.skipped?.[0]?.reason ?? "unknown reason";
        setFeedback({
          kind: "error",
          text: `Undo finished with problems — ${failedCount} file${failedCount === 1 ? "" : "s"} couldn't be moved back (${reason}). The rest were restored.`,
        });
      } else {
        const reason = res.failed?.[0]?.reason ?? res.skipped?.[0]?.reason ?? "unknown reason";
        setFeedback({ kind: "error", text: `Couldn't undo — ${reason}. Nothing was changed.` });
      }
    } catch (err) {
      if (err instanceof ApiError) {
        setFeedback({
          kind: "error",
          text: `Couldn't undo — ${errText(err)}. Nothing was changed.`,
        });
        return;
      }
      // No response was ever produced: the host's fetch rejects on a redirect, and a connection
      // dropped mid-POST rejects the same way. The server may already have moved part or all of the
      // batch back before the connection died, so the outcome is unknown — never report it as
      // "nothing was changed", which would tell the user there is nothing to re-check.
      setFeedback({
        kind: "error",
        text: `Couldn't confirm the undo — ${errText(err)}. Some files may already have been moved back; check the batch before trying again.`,
      });
    } finally {
      setUndoing(false);
      setConfirming(false);
      void loadSummary(); // re-read so the summary flips to consumed / "No rename to undo."
    }
  }

  return (
    <div className="rounded-xl border border-border bg-card p-4">
      <h3 className="text-base font-semibold text-foreground">Undo last rename</h3>
      <p className="mb-4 mt-1 text-sm text-secondary">
        This moves every file in that batch back to its original name. It can&apos;t be undone
        again. Only the most recent rename is kept, and a rename too large to record isn&apos;t kept
        at all — the dry run is the check before those.
      </p>

      {loading ? (
        <div className="flex items-center gap-2 text-sm text-secondary">
          <Spinner />
          Checking for a recent rename…
        </div>
      ) : summaryError ? (
        <div className="space-y-2">
          <StatusText kind="error">
            Couldn&apos;t check for a recent rename — {summaryError}.
          </StatusText>
          <div>
            <Button variant="ghost" onClick={() => void loadSummary()}>
              Retry
            </Button>
          </div>
        </div>
      ) : hasUndoable ? (
        <div className="space-y-3">
          <div className="flex items-center justify-between gap-3">
            <span className="text-sm text-foreground">
              Last rename: {count} item{count === 1 ? "" : "s"} renamed · {relativeTime(writtenMs)}
            </span>
            <Button
              variant="ghost"
              onClick={() => {
                setConfirming(true);
              }}
              disabled={undoing}
            >
              <Undo2 className="h-4 w-4" />
              Undo last rename
            </Button>
          </div>
          {feedback ? <StatusText kind={feedback.kind}>{feedback.text}</StatusText> : null}
        </div>
      ) : (
        <div className="space-y-2">
          <span className="text-sm text-secondary">No rename to undo.</span>
          {feedback ? (
            <div>
              <StatusText kind={feedback.kind}>{feedback.text}</StatusText>
            </div>
          ) : null}
        </div>
      )}

      {confirming ? (
        <Dialog
          titleId={UNDO_TITLE_ID}
          describedById={UNDO_DESC_ID}
          pending={undoing}
          onCancel={() => {
            setConfirming(false);
          }}
          size="sm"
        >
          <h2 id={UNDO_TITLE_ID} className="mb-2 text-lg font-semibold text-foreground">
            Undo last rename?
          </h2>
          <p id={UNDO_DESC_ID} className="mb-6 text-sm text-secondary">
            This moves {count} file{count === 1 ? "" : "s"} back to their original names. This
            can&apos;t be undone again.
          </p>
          <div className="flex justify-end gap-3">
            <button
              type="button"
              onClick={() => {
                setConfirming(false);
              }}
              disabled={undoing}
              className="px-4 py-2 text-sm text-secondary hover:text-foreground disabled:opacity-60"
            >
              Cancel
            </button>
            <button
              type="button"
              onClick={() => void onUndo()}
              disabled={undoing}
              className="inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-500 disabled:opacity-60"
            >
              {undoing ? <Spinner /> : null}
              Undo {count} rename{count === 1 ? "" : "s"}
            </button>
          </div>
        </Dialog>
      ) : null}
    </div>
  );
}

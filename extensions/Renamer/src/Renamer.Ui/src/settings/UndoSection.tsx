/**
 * The "Undo last rename" settings-panel section + its destructive confirm.
 *
 * Reads GET /last-batch on mount (and whenever `refreshKey` bumps — the Review dialog's success
 * callback bumps it). Gates POST /undo behind a red destructive confirm. Every sentence the user
 * reads here is composed by `undoLogic.ts`, where the suite covers it.
 *
 * SECURITY: reasons are rendered as React text nodes (auto-escaped).
 */
import { useCallback, useEffect, useState } from "react";
import { requestJson, ApiError } from "@cove-extensions/ui-shared/extensionRequest";
import { Undo2 } from "lucide-react";

import { Dialog } from "../common/ui/Dialog";
import { Button, StatusText, Spinner } from "@cove-extensions/ui-shared";
import { api } from "../common/lib/extension";
import type { LastBatchSummary, UndoResult } from "../wire/api";
import {
  buildUndoFeedback,
  buildUndoStatus,
  buildUndoUnconfirmed,
  type UndoFeedback,
} from "./undoLogic";

const LAST_BATCH_PATH = api("last-batch");
const UNDO_PATH = api("undo");

const UNDO_TITLE_ID = "rename-undo-confirm-title";
const UNDO_DESC_ID = "rename-undo-confirm-message";

function errText(err: unknown): string {
  return err instanceof ApiError ? `${err.status} ${err.body}` : String(err);
}

type Feedback = UndoFeedback | null;

export function UndoSection({ refreshKey }: { refreshKey: number }) {
  const [summary, setSummary] = useState<LastBatchSummary | null>(null);
  // The clock is read once, WITH the summary, rather than on every render. The expiry decision is
  // then a fact about the moment the data was fetched — which is what the data describes — instead of
  // something that can flip mid-render, and the render stays pure.
  const [loadedAtMs, setLoadedAtMs] = useState(0);
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
      setLoadedAtMs(Date.now());
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

  // The button acts on what is LEFT, not on what the batch started as: a partly restored batch is
  // still offered, and what it offers is the outstanding work. An expired batch keeps its line —
  // saying nothing there would read as "there was never a rename".
  const status = buildUndoStatus(summary, loadedAtMs);
  const remaining = status?.remaining ?? 0;
  const hasUndoable = status !== null && !status.expired;

  async function onUndo() {
    setUndoing(true);
    setFeedback(null);
    try {
      // /undo takes NO body, and answers every arm with counts — the "nothing open to undo" arm
      // included. So a bodyless reply is an anomaly rather than an outcome, and `requestJson` raises
      // it as one instead of resolving a success this panel would have to invent a sentence for.
      const res = await requestJson<UndoResult>(UNDO_PATH, { method: "POST" });
      // Composed by a pure module, not here: every figure in that sentence has to come from a total
      // and never from the length of the response's capped sample, and that is a claim a test can hold
      // and a render function cannot show.
      setFeedback(buildUndoFeedback(res));
    } catch (err) {
      if (err instanceof ApiError) {
        setFeedback({
          kind: "error",
          text: `Couldn't undo — ${errText(err)}. Nothing was changed.`,
        });
        return;
      }
      // No response the panel can read: the outcome of a destructive operation is unknown, and
      // `undoLogic` owns which sentence that earns.
      setFeedback(buildUndoUnconfirmed(errText(err)));
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
        Moves every file in that batch back to its original name. Only the newest rename is kept,
        and one too large to record is not kept at all.
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
            <span className="text-sm text-foreground">Last rename: {status.line}</span>
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
          <span className="text-sm text-secondary">
            {status ? `Last rename: ${status.line}` : "No rename to undo."}
          </span>
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
            This moves {remaining} file{remaining === 1 ? "" : "s"} back to their original names.
            This can&apos;t be undone again.
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
              Undo {remaining} rename{remaining === 1 ? "" : "s"}
            </button>
          </div>
        </Dialog>
      ) : null}
    </div>
  );
}

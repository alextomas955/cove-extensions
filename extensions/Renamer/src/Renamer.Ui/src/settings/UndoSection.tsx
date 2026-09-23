/**
 * The page's footer row: what the last rename was, the extension's id, and the undo control behind a
 * destructive confirm. Every sentence the user reads here is composed by `undoLogic.ts`.
 */
import { useState } from "react";
import { ConfirmDialog } from "@cove/runtime/components";
import { Undo2 } from "lucide-react";

import { Button, StatusText, Spinner } from "@cove-extensions/ui-shared";
import { EXTENSION_ID } from "../common/lib/extension";
import { buildUndoStatus, type UndoFeedback } from "./undoLogic";
import { useLastBatch } from "./useLastBatch";

type UndoStatus = ReturnType<typeof buildUndoStatus>;

// What the footer shows beside the extension id, in precedence order: the check in flight, the check
// failing, an undoable rename, then an expired one or none.
function UndoStatusRow({
  loading,
  summaryError,
  onRetry,
  status,
  hasUndoable,
  feedback,
  undoing,
  onUndo,
}: Readonly<{
  loading: boolean;
  summaryError: string | null;
  onRetry: () => void;
  status: UndoStatus;
  hasUndoable: boolean;
  feedback: UndoFeedback | null;
  undoing: boolean;
  onUndo: () => void;
}>) {
  if (loading) {
    return (
      <div className="flex items-center gap-2 text-sm text-secondary">
        <Spinner />
        Checking for a recent rename…
      </div>
    );
  }

  if (summaryError) {
    return (
      <div className="flex flex-wrap items-center justify-end gap-3">
        <StatusText kind="error">
          Couldn&apos;t check for a recent rename: {summaryError}.
        </StatusText>
        <Button variant="ghost" onClick={onRetry}>
          Retry
        </Button>
      </div>
    );
  }

  const feedbackLine = feedback ? (
    <StatusText kind={feedback.kind}>{feedback.text}</StatusText>
  ) : null;

  if (hasUndoable && status) {
    return (
      <div className="flex flex-wrap items-center justify-end gap-3">
        {feedbackLine}
        <span className="text-sm text-foreground">Last rename: {status.line}</span>
        <Button variant="ghost" onClick={onUndo} disabled={undoing}>
          <Undo2 className="h-4 w-4" />
          Undo last rename
        </Button>
      </div>
    );
  }

  return (
    <div className="flex flex-wrap items-center justify-end gap-3">
      {feedbackLine}
      <span className="text-sm text-secondary">
        {status ? `Last rename: ${status.line}` : "No rename to undo."}
      </span>
    </div>
  );
}

export function UndoSection({ refreshKey }: Readonly<{ refreshKey: number }>) {
  const {
    summary,
    loadedAtMs,
    loading,
    error: summaryError,
    reload,
    undo,
  } = useLastBatch(refreshKey);
  const [confirming, setConfirming] = useState(false);
  const [undoing, setUndoing] = useState(false);
  const [feedback, setFeedback] = useState<UndoFeedback | null>(null);

  // The button acts on what is left, not on what the batch started as. An expired batch keeps its
  // line: saying nothing there would read as "there was never a rename".
  const status = buildUndoStatus(summary, loadedAtMs);
  const remaining = status?.remaining ?? 0;
  const hasUndoable = status !== null && !status.expired;

  async function onUndo() {
    setUndoing(true);
    setFeedback(null);
    setFeedback(await undo());
    setUndoing(false);
    setConfirming(false);
  }

  return (
    <div
      id="rename-undo-section"
      className="flex flex-wrap items-center justify-between gap-3 border-t border-border pt-4"
    >
      <p className="text-xs text-secondary">
        Undo reverts the most recent rename that still has files to put back.{" "}
        <span aria-hidden="true">·</span> <span className="font-mono">{EXTENSION_ID}</span>
      </p>

      <div className="shrink-0">
        <UndoStatusRow
          loading={loading}
          summaryError={summaryError}
          onRetry={() => void reload()}
          status={status}
          hasUndoable={hasUndoable}
          feedback={feedback}
          undoing={undoing}
          onUndo={() => {
            setConfirming(true);
          }}
        />
      </div>

      <ConfirmDialog
        open={confirming}
        title="Undo last rename?"
        message={`This moves ${String(remaining)} file${remaining === 1 ? "" : "s"} back to their original names. This can't be undone again.`}
        confirmLabel={`Undo ${String(remaining)} rename${remaining === 1 ? "" : "s"}`}
        destructive
        isPending={undoing}
        onConfirm={onUndo}
        onCancel={() => {
          setConfirming(false);
        }}
      />
    </div>
  );
}

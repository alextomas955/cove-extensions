/**
 * The choice the videos selection bar opens: the five scene rows in their fixed order, and a way out
 * that sends nothing.
 *
 * Its own copy on the shared shell. It composes no sentence: every string it draws is declared once
 * in the copy module and the rows arrive already decided.
 */
import {
  BATCH_CHOOSE_AN_ACTION,
  BULK_CANCEL,
  BULK_CLOSE,
  BULK_REPORTS_IN_THE_JOB_DRAWER,
} from "../common/ui/copy";
import { ChoiceOverlay } from "../common/ui/ChoiceOverlay";
import type { BatchMenuRow } from "./batchMenuLogic";

export function WhisparrBatchChooser({
  rows,
  reason,
  onChoose,
}: {
  /** The rows offered, in the order they read. Empty when a refusal is being stated. */
  rows: readonly BatchMenuRow[];
  /** The one sentence stating a refusal, or null when rows are being offered. */
  reason: string | null;
  /** Called with the chosen row, or with null when the reader leaves without choosing. */
  onChoose: (row: BatchMenuRow | null) => void;
}) {
  return (
    <ChoiceOverlay<BatchMenuRow>
      label={BATCH_CHOOSE_AN_ACTION}
      reason={reason}
      rows={rows}
      footer={BULK_REPORTS_IN_THE_JOB_DRAWER}
      cancelLabel={BULK_CANCEL}
      closeLabel={BULK_CLOSE}
      onChoose={onChoose}
    />
  );
}

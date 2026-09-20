/**
 * The choice the videos selection bar opens: the five scene rows in their fixed order, and a way
 * out that sends nothing.
 */
import { Ban, CircleSlash, Plus, Radar, Search } from "lucide-react";

import { BULK_CANCEL, BULK_CLOSE } from "../common/ui/copy";
import { ChoiceOverlay, type RowIcon } from "../common/ui/ChoiceOverlay";
import type { BatchMenuRow } from "./batchMenuLogic";
import type { SceneBatchVerb } from "../wire/api";

// Total by type, so a verb added to the wire enum fails this build rather than rendering a row
// with no glyph.
const VERB_ICON: Record<NonNullable<SceneBatchVerb>, RowIcon> = {
  add: Plus,
  monitor: Radar,
  unmonitor: CircleSlash,
  search: Search,
  exclude: Ban,
};

export function WhisparrBatchChooser({
  rows,
  count,
  reason,
  onChoose,
}: {
  /** The rows offered, in the order they read. Empty when a refusal is being stated. */
  rows: readonly BatchMenuRow[];
  count: number;
  /** The sentence stating a refusal, or null when rows are being offered. */
  reason: string | null;
  /** Called with the chosen row, or with null when the reader leaves without choosing. */
  onChoose: (row: BatchMenuRow | null) => void;
}) {
  return (
    <ChoiceOverlay<BatchMenuRow & { icon: RowIcon }>
      count={count}
      reason={reason}
      rows={rows.map((row) => ({ ...row, icon: VERB_ICON[row.verb] }))}
      cancelLabel={BULK_CANCEL}
      closeLabel={BULK_CLOSE}
      onChoose={onChoose}
    />
  );
}

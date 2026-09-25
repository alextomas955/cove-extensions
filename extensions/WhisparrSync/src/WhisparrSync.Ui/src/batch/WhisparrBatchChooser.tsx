/** The choice the videos selection bar opens: the five scene rows, plus cancel. */
import { BULK_CANCEL, BULK_CLOSE } from "../common/ui/copy";
import { ChoiceOverlay, type RowIcon } from "../common/ui/ChoiceOverlay";
import { VERB_GLYPH, type WhisparrVerb } from "../common/ui/verbGlyphs";
import type { BatchMenuRow } from "./batchMenuLogic";
import type { SceneBatchVerb } from "../wire/api";

// Which shared verb each wire verb is. Total by type, so a verb added to the wire enum fails this
// build rather than rendering a row with no glyph.
const ROW_VERB: Record<NonNullable<SceneBatchVerb>, WhisparrVerb> = {
  add: "add",
  monitor: "monitor",
  unmonitor: "unmonitor",
  search: "search",
  exclude: "exclude",
};

export function WhisparrBatchChooser({
  rows,
  count,
  reason,
  onChoose,
}: Readonly<{
  /** Empty when a refusal is being stated. */
  rows: readonly BatchMenuRow[];
  count: number;
  /** A refusal sentence, or null when rows are being offered. */
  reason: string | null;
  /** Called with null when the overlay is left without a choice. */
  onChoose: (row: BatchMenuRow | null) => void;
}>) {
  return (
    <ChoiceOverlay<BatchMenuRow & { icon: RowIcon }>
      count={count}
      reason={reason}
      rows={rows.map((row) => ({ ...row, icon: VERB_GLYPH[ROW_VERB[row.verb]] }))}
      cancelLabel={BULK_CANCEL}
      closeLabel={BULK_CLOSE}
      onChoose={onChoose}
    />
  );
}

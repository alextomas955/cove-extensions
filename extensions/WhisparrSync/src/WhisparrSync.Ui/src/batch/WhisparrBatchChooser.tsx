/**
 * The choice the videos selection bar opens: the five scene rows in their fixed order, and a way out
 * that sends nothing.
 *
 * Its own copy on the shared shell. It composes no sentence: every string it draws is declared once
 * in the copy module and the rows arrive already decided.
 */
import { Ban, CircleSlash, Plus, Radar, Search } from "lucide-react";

import { BULK_CANCEL, BULK_CLOSE } from "../common/ui/copy";
import { ChoiceOverlay, type RowIcon } from "../common/ui/ChoiceOverlay";
import type { BatchMenuRow } from "./batchMenuLogic";
import type { SceneBatchVerb } from "../wire/api";

/**
 * The glyph each verb draws.
 *
 * Total by TYPE, so a verb added to the wire enum fails this build rather than rendering a row with
 * no glyph. It lives here rather than in the rules module, which draws nothing.
 */
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
  /** How many scenes the selection holds. */
  count: number;
  /** The one sentence stating a refusal, or null when rows are being offered. */
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

/**
 * The choice the entity selection bars open: one row per monitoring action the connected instance
 * can carry out over the whole selection, and a way out that sends nothing.
 *
 * Its own copy on the shared shell, so this surface and the scene batch each name their own rows
 * without either widening its props for the other.
 */
import { BULK_CANCEL, BULK_CLOSE } from "../common/ui/copy";
import { ChoiceOverlay, type RowIcon } from "../common/ui/ChoiceOverlay";
import { MONITOR_ITEM_ICON } from "./EntityMonitorMenu";
import type { BulkMonitorAction } from "./monitorMenuLogic";

export function BulkMonitorChoice({
  actions,
  count,
  reason,
  onChoose,
}: {
  /** The actions offered, in the order they read. Empty when there is nothing to offer. */
  actions: readonly BulkMonitorAction[];
  /** How many entities the selection holds. */
  count: number;
  /** The one sentence saying why nothing is offered, or null when something is. */
  reason: string | null;
  /** Called with the chosen action, or with null when the reader leaves without choosing. */
  onChoose: (action: BulkMonitorAction | null) => void;
}) {
  return (
    <ChoiceOverlay<BulkMonitorAction & { icon: RowIcon }>
      count={count}
      reason={reason}
      rows={actions.map((action) => ({ ...action, icon: MONITOR_ITEM_ICON[action.key] }))}
      cancelLabel={BULK_CANCEL}
      closeLabel={BULK_CLOSE}
      onChoose={onChoose}
    />
  );
}

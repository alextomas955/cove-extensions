/**
 * The one notice a screen shows for a reason its controls share.
 *
 * The prop shape takes the reason once and the number of controls it affects, so a caller cannot
 * express one notice per control - forty cards repeating one warning consume a quarter of every card
 * and teach the reader to skip it.
 *
 * No affected control means no notice at all, rather than an empty one: a notice with nothing behind
 * it states a constraint that is not in force.
 */
import { StatusText } from "@cove-extensions/ui-shared";

export function RefusalNotice({
  reason,
  affectedControls,
}: {
  reason: string;
  /** How many controls on this screen the reason applies to. */
  affectedControls: number;
}) {
  if (affectedControls < 1) {
    return null;
  }
  return (
    <div role="note" className="rounded-lg border border-border bg-card px-3 py-2">
      <StatusText kind="warning">{reason}</StatusText>
    </div>
  );
}

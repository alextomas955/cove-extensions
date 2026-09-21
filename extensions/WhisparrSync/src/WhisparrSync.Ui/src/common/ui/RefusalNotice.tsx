/**
 * The one notice a screen shows for a reason its controls share. The props take the reason once
 * and the number of controls it affects, so a caller cannot express one notice per control.
 *
 * No affected control means no element at all. An empty notice would state a constraint that is
 * not in force.
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

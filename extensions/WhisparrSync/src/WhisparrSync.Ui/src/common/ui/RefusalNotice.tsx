/**
 * The one notice a screen shows for a reason its controls share. The props take the reason once
 * and the number of controls it affects, so a caller cannot express one notice per control.
 *
 * No affected control means no element at all. An empty notice would state a constraint that is
 * not in force.
 */
import { TriangleAlert } from "lucide-react";
import { StatusText } from "@cove-extensions/ui-shared";

export function RefusalNotice({
  reason,
  affectedControls,
}: Readonly<{
  reason: string;
  /** How many controls on this screen the reason applies to. */
  affectedControls: number;
}>) {
  if (affectedControls < 1) {
    return null;
  }
  // Carried on a mark and a line of text rather than a panel of its own. Every screen this appears
  // on already stacks panels, and a third one between them reads as another section instead of as a
  // remark about the one below it. The host emits no tinted amber surface, so a notice that wanted
  // its own field would have to ship CSS.
  return (
    <div role="note" className="flex items-start gap-2 text-amber-400">
      <TriangleAlert className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
      <StatusText kind="warning">{reason}</StatusText>
    </div>
  );
}

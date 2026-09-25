/**
 * What a surface draws in place of a state while a run this browser started is still working
 * through the thing it describes.
 *
 * Not a state: a run acts on its entities one at a time and can refuse any of them, so nothing here
 * claims an outcome. What the instance ends up holding is read when the run stops, and that read is
 * what replaces this.
 *
 * Drawn as the same `StatusPill` a state is, because it takes a state's place on the card and two
 * shapes alternating in one slot read as two different kinds of thing.
 */
import { Spinner, StatusPill } from "@cove-extensions/ui-shared";

import { WORKING_IN_WHISPARR } from "./copy";

export function WorkingChip() {
  return (
    <StatusPill
      variant="gray"
      title={WORKING_IN_WHISPARR}
      icon={<Spinner className="h-3.5 w-3.5" />}
    >
      {WORKING_IN_WHISPARR}
    </StatusPill>
  );
}

/**
 * What a surface draws in place of a state while a run this browser started is still working
 * through the thing it describes.
 *
 * Not a state: a run acts on its entities one at a time and can refuse any of them, so nothing here
 * claims an outcome. What the instance ends up holding is read when the run stops, and that read is
 * what replaces this.
 */
import { Loader } from "lucide-react";

import { WORKING_IN_WHISPARR } from "./copy";

const CHIP_CLASS =
  "inline-flex items-center gap-1 rounded-full border border-border px-2 py-0.5 text-xs text-secondary";

export function WorkingChip() {
  return (
    <span className={CHIP_CLASS} title={WORKING_IN_WHISPARR}>
      <Loader className="h-3 w-3 animate-spin" aria-hidden="true" />
      {WORKING_IN_WHISPARR}
    </span>
  );
}

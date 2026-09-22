/**
 * What a surface draws for an entity the library holds no usable link for.
 *
 * Not a state the instance is in: the connected generation names entities in one source's
 * namespace, the library carries no identifier in it for this one, and so the instance was never
 * asked. Drawn rather than left blank, because a card with no badge among cards that have one
 * reads as an oversight and cannot be told from one still being read.
 */
import { Unlink } from "lucide-react";

import { NOT_LINKED, NOT_LINKED_REASON } from "./copy";

const CHIP_CLASS =
  "inline-flex items-center gap-1 rounded-full border border-border px-2 py-0.5 text-xs text-secondary";

export function NotLinkedChip() {
  return (
    <span className={CHIP_CLASS} title={NOT_LINKED_REASON}>
      <Unlink className="h-3 w-3" aria-hidden="true" />
      {NOT_LINKED}
    </span>
  );
}

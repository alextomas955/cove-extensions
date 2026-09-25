/**
 * What a surface draws for an entity the library holds no usable link for.
 *
 * Not a state the instance is in: the connected generation names entities in one source's
 * namespace, the library carries no identifier in it for this one, and so the instance was never
 * asked. Drawn rather than left blank, because a card with no badge among cards that have one
 * reads as an oversight and cannot be told from one still being read.
 *
 * Drawn as the same `StatusPill` a state is, so a reader meets one badge family rather than two.
 */
import { StatusPill } from "@cove-extensions/ui-shared";

import { NOT_LINKED_REASON } from "./copy";
import { StateGlyph } from "./StateGlyph";
import { NOT_LINKED_MARKER } from "./stateVocabularyLogic";

export function NotLinkedChip() {
  return (
    <StatusPill
      variant={NOT_LINKED_MARKER.variant}
      title={NOT_LINKED_REASON}
      icon={<StateGlyph iconKey={NOT_LINKED_MARKER.iconKey} />}
    >
      {NOT_LINKED_MARKER.label}
    </StatusPill>
  );
}

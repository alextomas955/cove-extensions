/**
 * One entity state, rendered as the shared `StatusPill` with the vocabulary's glyph in its `icon`
 * slot - the prop exists so a status never rides on colour alone, which is what this chip needs.
 *
 * The glyph is hidden from assistive technology because the label beside it already carries the
 * meaning; the glyph is what distinguishes two states that share a tint on screen.
 */
import { StatusPill } from "@cove-extensions/ui-shared";

import { describeState, renameState, type WhisparrEntityState } from "./stateVocabularyLogic";

export function StateChip({
  state,
  label,
}: {
  state: WhisparrEntityState;
  /** The name this view uses for the state. The glyph and the tint are unchanged. */
  label?: string;
}) {
  const description = label === undefined ? describeState(state) : renameState(state, label);
  return (
    <StatusPill
      variant={description.variant}
      icon={<span aria-hidden="true">{description.glyph}</span>}
    >
      {description.label}
    </StatusPill>
  );
}

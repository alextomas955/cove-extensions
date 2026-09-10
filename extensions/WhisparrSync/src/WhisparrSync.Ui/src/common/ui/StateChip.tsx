/**
 * One entity state, rendered as the shared `StatusPill` with the vocabulary's glyph in its `icon`
 * slot - the prop exists so a status never rides on colour alone, which is what this chip needs.
 *
 * The glyph takes the pill's own colour, so the mark and the tint cannot disagree about a state.
 */
import { StatusPill } from "@cove-extensions/ui-shared";

import { StateGlyph } from "./StateGlyph";
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
    <StatusPill variant={description.variant} icon={<StateGlyph iconKey={description.iconKey} />}>
      {description.label}
    </StatusPill>
  );
}

/**
 * One entity state, rendered as the shared `StatusPill` with the vocabulary's glyph in its `icon`
 * slot, so a status never rides on colour alone.
 */
import { StatusPill } from "@cove-extensions/ui-shared";

import { StateGlyph } from "./StateGlyph";
import { describeState, renameState, type WhisparrEntityState } from "./stateVocabularyLogic";

export function StateChip({
  state,
  label,
}: {
  state: WhisparrEntityState;
  /** The name this view uses for the state. The glyph and the tint stay as they are. */
  label?: string;
}) {
  const description = label === undefined ? describeState(state) : renameState(state, label);
  return (
    <StatusPill variant={description.variant} icon={<StateGlyph iconKey={description.iconKey} />}>
      {description.label}
    </StatusPill>
  );
}

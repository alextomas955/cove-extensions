/**
 * One entity state, rendered as the shared `StatusPill` with the vocabulary's glyph in its `icon`
 * slot, so a status never rides on colour alone.
 */
import { StatusPill } from "@cove-extensions/ui-shared";

import { StateGlyph } from "./StateGlyph";
import { describeState, type WhisparrEntityState } from "./stateVocabularyLogic";

export function StateChip({ state }: Readonly<{ state: WhisparrEntityState }>) {
  const description = describeState(state);
  return (
    <StatusPill variant={description.variant} icon={<StateGlyph iconKey={description.iconKey} />}>
      {description.label}
    </StatusPill>
  );
}

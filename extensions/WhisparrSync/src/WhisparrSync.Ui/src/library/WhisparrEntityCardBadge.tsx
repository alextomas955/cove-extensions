/**
 * The Whisparr status on one library card, drawn in the host's own clipped in-card box.
 *
 * The host spreads its slot context as top-level props, so each exported component's props are
 * exactly what that context carries. A read through a nested context object throws into the card's
 * error boundary. Only the Cove id is declared and only the Cove id is read: the identifier the
 * instance is given is re-resolved on the server from the library's own identity row.
 *
 * A card the extension cannot speak for draws nothing at all, and so does a card whose read has not
 * answered. Nothing here is a spinner and nothing here is a second row: the host clips this box, and
 * a wrapped label disappears below the clip with no error.
 *
 * The unknown state is the Missing tab's, and a badge here never draws it: a read that established
 * nothing draws nothing, and the reason is stated once for the page on the toolbar control that
 * asked for it.
 */
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import { StateChip } from "../common/ui/StateChip";
import { deriveState, type WhisparrEntityState } from "../common/ui/stateVocabularyLogic";
import type { LibraryCardKind } from "./cardStatusStore";
import { BADGE_STRIP_CLASS } from "./libraryClasses";
import { useLibraryStatusOn } from "./libraryToggleStore";
import { useCardStatus } from "./useCardStatus";

export function WhisparrStudioCardBadge({ studio }: { studio: { id: number } }) {
  return <EntityCardBadge kind="studio" coveId={studio.id} />;
}

function EntityCardBadge({ kind, coveId }: { kind: LibraryCardKind; coveId: number }) {
  const on = useLibraryStatusOn();
  const { reading, settled } = useCardStatus(kind, coveId, on);

  const state: WhisparrEntityState | null = reading === null ? null : deriveState(reading);
  const drawn = state === "statusUnknown" ? null : state;

  const region = deriveAsyncRegionState({
    reading: on && !settled,
    failed: false,
    hasContent: drawn !== null,
  });

  return (
    <AsyncRegion
      state={region}
      available={on}
      // Nothing while the batch is in flight. Forty placeholders inside forty clipped boxes is noise
      // on a surface whose whole purpose is a glance, and the cost is one reflow at a moment the
      // reader asked for.
      reading={null}
      empty={null}
      failed={null}
      content={
        drawn === null ? null : (
          <div className={BADGE_STRIP_CLASS}>
            <StateChip state={drawn} />
          </div>
        )
      }
    />
  );
}

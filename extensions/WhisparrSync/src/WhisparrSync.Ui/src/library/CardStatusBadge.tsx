/**
 * The Whisparr status on one library card, drawn in the host's own clipped in-card box.
 *
 * One component behind all three card kinds, taking the kind and the Cove id its caller declared.
 * The exported slot components sit beside it and each declares its own host prop at its narrowest.
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
import type { LibraryCardKind } from "../wire/api";
import { BADGE_STRIP_CLASS } from "./libraryClasses";
import { useLibraryStatusOn } from "./libraryToggleStore";
import { useCardStatus } from "./useCardStatus";

export function CardStatusBadge({ kind, coveId }: { kind: LibraryCardKind; coveId: number }) {
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

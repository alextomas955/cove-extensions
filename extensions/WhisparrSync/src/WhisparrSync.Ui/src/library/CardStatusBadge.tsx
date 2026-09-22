/**
 * The Whisparr status on one library card, for all three card kinds.
 *
 * The host clips this box, so the badge is one row and never wraps. A card with no reading draws
 * nothing; the reason is stated once for the page on the toolbar control.
 */
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import { StateChip } from "../common/ui/StateChip";
import { NotLinkedChip } from "../common/ui/NotLinkedChip";
import { WorkingChip } from "../common/ui/WorkingChip";
import { deriveState, type WhisparrEntityState } from "../common/ui/stateVocabularyLogic";
import type { LibraryCardKind } from "../wire/api";
import { badgeChipFor } from "./badgeChipLogic";
import { BADGE_STRIP_CLASS } from "./libraryClasses";
import { useLibraryStatusOn } from "./libraryToggleStore";
import { useCardStatus } from "./useCardStatus";

export function CardStatusBadge({ kind, coveId }: { kind: LibraryCardKind; coveId: number }) {
  const on = useLibraryStatusOn();
  const { reading, settled, running } = useCardStatus(kind, coveId, on);

  const state: WhisparrEntityState | null = reading === null ? null : deriveState(reading);
  const drawn = state === "statusUnknown" ? null : state;

  const chip = badgeChipFor({ running, settled, state: drawn });

  const region = deriveAsyncRegionState({
    reading: on && !settled,
    failed: false,
    // Whatever the badge has to draw, not only a state: a card the library holds no link for has
    // a chip of its own, and an empty region would swallow it.
    hasContent: chip !== null,
  });

  return (
    <AsyncRegion
      state={region}
      available={on}
      // Nothing while the batch is in flight. A placeholder in every clipped box is noise on a
      // surface meant to be read at a glance.
      reading={null}
      empty={null}
      failed={null}
      content={
        chip === null ? null : (
          <div className={BADGE_STRIP_CLASS}>
            {chip === "working" ? (
              <WorkingChip />
            ) : chip === "notLinked" ? (
              <NotLinkedChip />
            ) : drawn === null ? null : (
              <StateChip state={drawn} />
            )}
          </div>
        )
      }
    />
  );
}

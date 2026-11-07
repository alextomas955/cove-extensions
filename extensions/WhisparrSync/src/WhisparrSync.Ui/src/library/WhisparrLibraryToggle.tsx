/**
 * The control in a list toolbar that shows or hides the Whisparr status on every card of the page.
 *
 * One component serves all three list-page registrations; it reads no slot context and takes no
 * props. It is never disabled: an unreachable instance changes what it says, not whether it works,
 * and pressing it again re-issues the batch.
 */
import { useEffect, useState, useSyncExternalStore } from "react";

import {
  HIDE_WHISPARR_STATUS,
  NO_PLACE_FOR_A_CARD_STATUS_HERE,
  SHOW_WHISPARR_STATUS,
} from "../common/ui/copy";
import { WhisparrLogo } from "../common/ui/WhisparrLogo";
import { cardStatusRefusal, registeredCardCount, subscribeCardStatus } from "./cardStatusStore";
import { libraryRefusalSentence } from "./libraryRefusalLogic";
import {
  TOGGLE_CLASS,
  TOGGLE_MARK_CLASS,
  TOGGLE_MARK_OFF_CLASS,
  TOGGLE_OFF_CLASS,
  TOGGLE_ON_CLASS,
} from "./libraryClasses";
import { toggleLibraryStatus, useLibraryStatusOn } from "./libraryToggleStore";

// A badge registers in its own effect and the coalescer folds the registrations one tick later, so
// a count read as the control turns on is the count of badges that had not mounted yet.
function useRegistrationsSettled(on: boolean): boolean {
  const [settled, setSettled] = useState(false);

  useEffect(() => {
    const settle = setTimeout(() => {
      setSettled(on);
    });
    return () => {
      clearTimeout(settle);
    };
  }, [on]);

  return settled;
}

export function WhisparrLibraryToggle() {
  const on = useLibraryStatusOn();
  const refusal = useSyncExternalStore(subscribeCardStatus, cardStatusRefusal);
  const registered = useSyncExternalStore(subscribeCardStatus, registeredCardCount);
  const settled = useRegistrationsSettled(on);

  const name = on ? HIDE_WHISPARR_STATUS : SHOW_WHISPARR_STATUS;

  // A control that is off makes no claim about any card, so it states no reason. A reason left on
  // it would survive a page turn, because nothing registers to clear it.
  //
  // The control cannot ask whether the instance is reachable: the connection test is at a tier its
  // reader does not hold. The fact arrives only as a refusal from the batch it triggered.
  //
  // The host mounts a card slot in its grid display mode only, so a mode where no badge can appear
  // is a mode where no card registered.
  //
  // With no visible label the accessible name is the only name there is, so the name leads and the
  // reason follows it.
  const pageReason =
    settled && registered === 0 ? NO_PLACE_FOR_A_CARD_STATUS_HERE : libraryRefusalSentence(refusal);
  const reason = on ? pageReason : null;
  const spoken = reason === null ? name : `${name}. ${reason}`;

  return (
    <button
      type="button"
      onClick={() => {
        toggleLibraryStatus();
      }}
      aria-pressed={on}
      title={spoken}
      aria-label={spoken}
      className={`${TOGGLE_CLASS} ${on ? TOGGLE_ON_CLASS : TOGGLE_OFF_CLASS}`}
    >
      <WhisparrLogo
        className={on ? TOGGLE_MARK_CLASS : `${TOGGLE_MARK_CLASS} ${TOGGLE_MARK_OFF_CLASS}`}
      />
    </button>
  );
}

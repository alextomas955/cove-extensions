/**
 * The control in a list toolbar that shows or hides the Whisparr status on every card of the page.
 *
 * One component serves all three list-page registrations. It reads no slot context and takes no
 * props, so there is nothing to differentiate, and three registered names would be three chances at
 * a control that never renders and reports nothing.
 *
 * It is never disabled. An unreachable instance changes what it says, not whether it works, and
 * pressing it again re-issues the batch.
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

/**
 * Whether enough time has passed for every badge on the page to have registered.
 *
 * A badge registers in its own effect and the coalescer folds the registrations one tick later, so a
 * count read as the control turns on is the count of badges that had not mounted yet.
 */
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

  // The control cannot ask whether the instance is reachable: the connection test is at a tier this
  // control's reader does not hold. The fact arrives only as a refusal from the batch the control
  // itself triggered, so there is no unreachable state before it is pressed.
  //
  // The host mounts a card slot in its grid display mode only and this control sits in the toolbar
  // of every mode, so a mode where no badge can appear is a mode where no card registered.
  //
  // With no visible label the accessible name is the only name it has, so the name leads and the
  // reason follows it, matching the order the entity control uses.
  const reason =
    on && settled && registered === 0
      ? NO_PLACE_FOR_A_CARD_STATUS_HERE
      : libraryRefusalSentence(refusal);
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

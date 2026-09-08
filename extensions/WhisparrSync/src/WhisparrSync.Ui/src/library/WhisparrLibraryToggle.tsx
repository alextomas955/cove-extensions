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
import { useSyncExternalStore } from "react";

import {
  HIDE_WHISPARR_STATUS,
  SHOW_WHISPARR_STATUS,
  WHISPARR_STATUS_COULD_NOT_BE_READ,
} from "../common/ui/copy";
import { WhisparrLogo } from "../common/ui/WhisparrLogo";
import { cardStatusRefusal, subscribeCardStatus } from "./cardStatusStore";
import {
  TOGGLE_CLASS,
  TOGGLE_MARK_CLASS,
  TOGGLE_MARK_OFF_CLASS,
  TOGGLE_OFF_CLASS,
  TOGGLE_ON_CLASS,
} from "./libraryClasses";
import { toggleLibraryStatus, useLibraryStatusOn } from "./libraryToggleStore";

export function WhisparrLibraryToggle() {
  const on = useLibraryStatusOn();
  const refusal = useSyncExternalStore(subscribeCardStatus, cardStatusRefusal);

  const name = on ? HIDE_WHISPARR_STATUS : SHOW_WHISPARR_STATUS;

  // The control cannot ask whether the instance is reachable: the connection test is at a tier this
  // control's reader does not hold. The fact arrives only as a refusal from the batch the control
  // itself triggered, so there is no unreachable state before it is pressed.
  //
  // With no visible label the accessible name is the only name it has, so the name leads and the
  // reason follows it, matching the order the entity control uses.
  const spoken = refusal === "none" ? name : `${name}. ${WHISPARR_STATUS_COULD_NOT_BE_READ}`;

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

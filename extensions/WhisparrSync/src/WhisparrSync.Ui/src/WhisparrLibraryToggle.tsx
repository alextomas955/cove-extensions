/**
 * The "show Whisparr status" pill on the videos / studios / performers list toolbars, consistent with Cove's
 * grid/list/wall buttons and OFF by default. Toggling it gates the per-card badges (and, on videos, reveals the
 * count row via the `videos-list-row` slot). Every instance shares one on/off state through
 * {@link ./libraryToggleStore}, since each toolbar renders a separate component instance.
 *
 * Reads NO slot context (a `props.context.*` read crashes the host) and takes no props.
 */
import { toggleLibraryStatus, useLibraryStatusOn } from "./libraryToggleStore";
import { WhisparrLogo } from "./WhisparrLogo";

export function WhisparrLibraryToggle() {
  const on = useLibraryStatusOn();

  return (
    <button
      type="button"
      onClick={() => {
        toggleLibraryStatus();
      }}
      aria-pressed={on}
      title={on ? "Hide Whisparr status" : "Show Whisparr status"}
      aria-label={on ? "Hide Whisparr status" : "Show Whisparr status"}
      className={`inline-flex items-center justify-center rounded-lg p-2 transition-colors ${
        on ? "text-accent" : "text-secondary hover:text-foreground"
      }`}
    >
      <WhisparrLogo className={`h-4 w-4 ${on ? "" : "opacity-60"}`} />
    </button>
  );
}

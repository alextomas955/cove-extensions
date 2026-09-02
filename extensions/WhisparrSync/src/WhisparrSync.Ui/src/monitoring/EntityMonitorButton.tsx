/**
 * The Whisparr control in a studio page's own action row.
 *
 * The host spreads its slot context as top-level props, so the props here are exactly what that
 * context carries. Only the Cove id is declared and only the Cove id is read: the identifier the
 * instance is given is re-resolved on the server from the library's own identity row, and sending one
 * from the browser is what the product forbids outright.
 *
 * The host's `Studio` type cannot be generated into this bundle's wire types, which are emitted from
 * this extension's own registrations, so this one prop shape is hand-declared at its narrowest and
 * its field name is pinned in a test against the host source.
 */
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import { MONITOR_IN_WHISPARR, MONITORED_IN_WHISPARR } from "../common/ui/copy";
import { WhisparrMark } from "./WhisparrMark";
import { useMonitoring } from "./useMonitoring";

/**
 * Cove's own action-row button styling, copied verbatim from the host.
 *
 * The host exports it so extensions match its rounding, size and border, and it cannot be imported
 * across repositories. Every class in it is one the host's own source writes, which is what makes it
 * a class the host actually emits.
 */
const HERO_ACTION_BUTTON_CLASS =
  "inline-flex h-10 w-10 items-center justify-center rounded-lg border border-border bg-card transition-colors hover:border-accent hover:text-foreground disabled:cursor-not-allowed";

export function EntityMonitorButton({ studio }: { studio: { id: number } }) {
  const { state, monitor } = useMonitoring("studio", studio.id);
  const monitored = state.view?.monitored === true;
  const name = monitored ? MONITORED_IN_WHISPARR : MONITOR_IN_WHISPARR;

  return (
    <button
      type="button"
      className={
        monitored
          ? `relative ${HERO_ACTION_BUTTON_CLASS} border-accent`
          : `relative ${HERO_ACTION_BUTTON_CLASS}`
      }
      // One action in flight at a time, so one entity cannot have two monitors on the way.
      disabled={state.acting}
      aria-label={name}
      title={name}
      onClick={() => {
        monitor("futureScenes");
      }}
    >
      <AsyncRegion
        state={deriveAsyncRegionState(state.read)}
        // Until the read answers, the bordered shell alone. The mark is full colour and tracks the
        // connected generation, so painting one before the read would be a chance of showing the
        // wrong generation's colour. A failed read is the same shell for the same reason.
        reading={null}
        empty={null}
        failed={null}
        content={
          <>
            <WhisparrMark className="h-5 w-5" />
            {monitored ? (
              <svg
                viewBox="0 0 24 24"
                className="absolute bottom-0 right-0 h-3 w-3 rounded-full bg-card text-accent"
                aria-hidden="true"
                focusable="false"
                role="presentation"
                fill="none"
                stroke="currentColor"
                strokeWidth="3"
                strokeLinecap="round"
                strokeLinejoin="round"
              >
                <path d="M20 6 9 17l-5-5" />
              </svg>
            ) : null}
          </>
        }
      />
    </button>
  );
}

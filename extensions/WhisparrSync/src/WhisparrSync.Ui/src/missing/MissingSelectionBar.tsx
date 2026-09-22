/**
 * The bar above the grid while scenes are ticked.
 *
 * Laid out as Cove's own list selection bar: the count on the left, the gestures centred, the verbs
 * beside them. The two bars sit on the same page one tab apart, so a reader meets one shape.
 *
 * Mounted whether or not anything is ticked, because the three key sequences are registered here.
 *
 * No control carries a border of its own: the host declares `border-border` twice and the later
 * `border` shorthand resets the colour. Focus uses `focus:ring-*`, because the host stylesheet
 * emits no `focus-visible` ring utility.
 */
import { useMemo } from "react";
import { Bookmark, BookmarkX, Loader2 } from "lucide-react";

import { WAITING_FOR_WHISPARR, selectionCount } from "../common/ui/copy";
import { OFF_SCREEN } from "../common/ui/offScreen";
import { useKeySequence } from "./hostComponents";
import {
  MONITOR_SELECTION_LABEL,
  UNMONITOR_SELECTION_LABEL,
  selectionActionsFor,
  selectionOutcomeLine,
  type SelectionOutcome,
} from "./missingSelectionLogic";

// Cove's own selection bar, to the class: one column on a narrow page and three where there is
// room, with the gestures in the middle one.
const BAR_CLASS =
  "mx-1 mt-1 grid grid-cols-1 items-center gap-2 rounded-lg border border-border bg-card/80 " +
  "px-3 py-1.5 sm:grid-cols-[1fr_auto_1fr]";

const COUNT_CLASS = "flex min-w-0 flex-wrap items-center gap-x-2 text-xs text-secondary";
const GESTURES_CLASS = "flex flex-wrap items-center justify-center gap-3";

const FOCUS_RING = "focus:outline-none focus:ring-2 focus:ring-accent";

// The first gesture is tinted and the rest are not.
const LEADING_GESTURE_CLASS = `rounded text-xs text-accent hover:underline ${FOCUS_RING}`;
const GESTURE_CLASS = `rounded text-xs text-secondary hover:text-foreground ${FOCUS_RING}`;

// Green, as Cove tints the one action in its own bar that acts on the item rather than editing it.
// The gestures beside it stay plain text, so the verb is the only coloured control.
const VERB_CLASS =
  `flex items-center gap-1 rounded px-2 py-0.5 text-xs text-green-400 hover:text-green-300 ` +
  `hover:bg-green-900/20 disabled:opacity-60 ${FOCUS_RING}`;

export function MissingSelectionBar({
  loadedPageIds,
  selected,
  outcome,
  runUnderWay,
  onSelect,
  onMonitorSelection,
  onUnmonitorSelection,
}: {
  /** The scenes on screen, in the order they are drawn. */
  loadedPageIds: readonly string[];
  selected: ReadonlySet<string>;
  outcome: SelectionOutcome;
  /** Whether a run this browser started is still going. */
  runUnderWay: boolean;
  /** The ids are always a subset of the loaded page. */
  onSelect: (ids: readonly string[]) => void;
  onMonitorSelection: () => void;
  onUnmonitorSelection: () => void;
}) {
  const actions = selectionActionsFor(loadedPageIds, selected);
  const refusal = selectionOutcomeLine(outcome);
  // The press settles in milliseconds and the run it started goes on, so the controls wait on
  // the run: a bar that freed itself at the press would invite a second run over the same scenes.
  const inFlight = outcome.kind === "inFlight" || runUnderWay;

  const bindings = useMemo(
    () =>
      actions.map((action) => ({
        id: action.shortcutId,
        keys: action.keys,
        surface: "list" as const,
        action: () => {
          onSelect(action.resulting);
        },
      })),
    [actions, onSelect],
  );
  useKeySequence(bindings);

  return (
    <div className="mb-3">
      {selected.size === 0 ? null : (
        <div className={BAR_CLASS}>
          <div className={COUNT_CLASS}>
            <span>{selectionCount(selected.size)}</span>
          </div>
          <div className={GESTURES_CLASS}>
            {actions.map((action, index) => (
              <button
                key={action.key}
                type="button"
                className={index === 0 ? LEADING_GESTURE_CLASS : GESTURE_CLASS}
                onClick={() => {
                  onSelect(action.resulting);
                }}
              >
                {action.label}
              </button>
            ))}
            {/* The name leads and the reason follows it off screen, so a disabled control still
                announces why. */}
            <button
              type="button"
              className={VERB_CLASS}
              title={inFlight ? WAITING_FOR_WHISPARR : undefined}
              disabled={inFlight}
              onClick={onMonitorSelection}
            >
              {inFlight ? (
                <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />
              ) : (
                <Bookmark className="h-3 w-3" aria-hidden="true" />
              )}
              {MONITOR_SELECTION_LABEL}
              {inFlight ? <span style={OFF_SCREEN}>{WAITING_FOR_WHISPARR}</span> : null}
            </button>
            <button
              type="button"
              className={VERB_CLASS}
              title={inFlight ? WAITING_FOR_WHISPARR : undefined}
              disabled={inFlight}
              onClick={onUnmonitorSelection}
            >
              {inFlight ? (
                <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />
              ) : (
                <BookmarkX className="h-3 w-3" aria-hidden="true" />
              )}
              {UNMONITOR_SELECTION_LABEL}
              {inFlight ? <span style={OFF_SCREEN}>{WAITING_FOR_WHISPARR}</span> : null}
            </button>
          </div>
          {/* The third column, which Cove's bar leaves empty so the gestures sit centred. */}
          <div className="hidden sm:block" aria-hidden="true" />
        </div>
      )}
      <p role="status" aria-live="polite" className="mt-1 text-sm text-muted">
        {refusal}
      </p>
    </div>
  );
}

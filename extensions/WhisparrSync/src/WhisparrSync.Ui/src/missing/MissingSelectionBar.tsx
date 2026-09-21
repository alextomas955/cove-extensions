/**
 * The bar above the grid while scenes are ticked.
 *
 * Mounted whether or not anything is ticked, because the three key sequences are registered here.
 *
 * No control carries a border of its own: the host declares `border-border` twice and the later
 * `border` shorthand resets the colour. Focus uses `focus:ring-*`, because the host stylesheet
 * emits no `focus-visible` ring utility.
 */
import { useMemo } from "react";
import { Bookmark, Loader2 } from "lucide-react";

import {
  BULK_REPORTS_IN_THE_JOB_DRAWER,
  WAITING_FOR_WHISPARR,
  selectionCount,
} from "../common/ui/copy";
import { OFF_SCREEN } from "../common/ui/offScreen";
import { useKeySequence } from "./hostComponents";
import {
  MONITOR_SELECTION_LABEL,
  selectionActionsFor,
  selectionOutcomeLine,
  type SelectionOutcome,
} from "./missingSelectionLogic";

const BAR_CLASS =
  "mx-1 mt-1 flex flex-wrap items-center gap-3 rounded-lg border border-border bg-card/80 px-3 py-1.5";

const FOCUS_RING = "focus:outline-none focus:ring-2 focus:ring-accent";

// The first gesture is tinted and the rest are not.
const LEADING_GESTURE_CLASS = `rounded text-xs text-accent hover:underline ${FOCUS_RING}`;
const GESTURE_CLASS = `rounded text-xs text-secondary hover:text-foreground ${FOCUS_RING}`;

const VERB_CLASS =
  `flex items-center gap-1 rounded px-2 py-0.5 text-xs text-accent hover:bg-accent/10 ` +
  `disabled:opacity-60 ${FOCUS_RING}`;

export function MissingSelectionBar({
  loadedPageIds,
  selected,
  outcome,
  onSelect,
  onMonitorSelection,
}: {
  /** The scenes on screen, in the order they are drawn. */
  loadedPageIds: readonly string[];
  selected: ReadonlySet<string>;
  outcome: SelectionOutcome;
  /** The ids are always a subset of the loaded page. */
  onSelect: (ids: readonly string[]) => void;
  onMonitorSelection: () => void;
}) {
  const actions = selectionActionsFor(loadedPageIds, selected);
  const refusal = selectionOutcomeLine(outcome);
  const inFlight = outcome.kind === "inFlight";

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
          <span className="text-xs text-secondary">{selectionCount(selected.size)}</span>
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
          {/* Its own full-width row, so it does not read as a run-on against the verbs. */}
          <span className="w-full text-xs text-secondary">{BULK_REPORTS_IN_THE_JOB_DRAWER}</span>
        </div>
      )}
      <p role="status" aria-live="polite" className="mt-1 text-sm text-muted">
        {refusal}
      </p>
    </div>
  );
}

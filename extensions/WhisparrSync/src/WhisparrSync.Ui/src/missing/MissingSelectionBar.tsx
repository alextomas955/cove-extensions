/**
 * The bar above the grid while scenes are ticked, and the one thing this tab does with a selection.
 *
 * Mounted whether or not anything is ticked, because the three key sequences are registered here and
 * a reader must be able to select the page without reaching for the pointer first.
 *
 * Every control is flat: a word, or a mark and a word, tinted by what it does. The bar sits among
 * Cove's own chrome directly above a grid of cards, and a row of filled boxes there reads as heavier
 * than the cards it acts on. Nothing here carries a border of its own, which is also what keeps it
 * off `border-border`: the host declares that utility twice and the later `border` shorthand resets
 * the colour, so a bordered control in this bundle draws its border in the text colour it inherits.
 *
 * `focus:ring-2 focus:ring-accent` rather than the focus-visible pair: the host stylesheet emits no
 * `focus-visible` ring utility, so that pair would paint nothing at all.
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

/** The bar's own shell, which is the shell the count row above the library cards wears. */
const BAR_CLASS =
  "mx-1 mt-1 flex flex-wrap items-center gap-3 rounded-lg border border-border bg-card/80 px-3 py-1.5";

/** The focus ring every control in the bar takes, so none of them is reachable and unmarked. */
const FOCUS_RING = "focus:outline-none focus:ring-2 focus:ring-accent";

/**
 * A selection gesture: a word alone.
 *
 * The first is tinted and the rest are not, because they are one set of gestures over the same page
 * and a row of equally tinted words has nothing leading it.
 */
const LEADING_GESTURE_CLASS = `rounded text-xs text-accent hover:underline ${FOCUS_RING}`;
const GESTURE_CLASS = `rounded text-xs text-secondary hover:text-foreground ${FOCUS_RING}`;

/** The verb: a mark and a word, tinted by the thing it does. */
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
  /** What the last press produced. */
  outcome: SelectionOutcome;
  /** Applies one gesture's answer, which is always a subset of the loaded page. */
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
          {/* The name leads and the reason follows it, off screen: forty controls sharing one reason
              must not each read it out, and a dimmed control with nothing to hear is a defect. */}
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
          {/* Its own full-width row. Beside the verbs it lands at whatever width is left and reads
              as a run-on against them. */}
          <span className="w-full text-xs text-secondary">{BULK_REPORTS_IN_THE_JOB_DRAWER}</span>
        </div>
      )}
      <p role="status" aria-live="polite" className="mt-1 text-sm text-muted">
        {refusal}
      </p>
    </div>
  );
}

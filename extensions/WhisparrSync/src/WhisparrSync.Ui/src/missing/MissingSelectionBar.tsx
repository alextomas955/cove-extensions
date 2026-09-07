/**
 * The bar above the grid while scenes are ticked, and the one thing this tab does with a selection.
 *
 * Mounted whether or not anything is ticked, because the three key sequences are registered here and
 * a reader must be able to select the page without reaching for the pointer first.
 *
 * `focus:ring-2 focus:ring-accent` rather than the focus-visible pair: the host stylesheet emits no
 * `focus-visible` ring utility, so that pair would paint nothing at all.
 */
import { useMemo } from "react";

import {
  BULK_REPORTS_IN_THE_JOB_DRAWER,
  WAITING_FOR_WHISPARR,
  selectionCount,
} from "../common/ui/copy";
import { OptionallyDisabled } from "../common/ui/DisabledControl";
import { useKeySequence } from "./hostComponents";
import {
  MONITOR_SELECTION_LABEL,
  selectionActionsFor,
  selectionOutcomeLine,
  type SelectionOutcome,
} from "./missingSelectionLogic";

/** The class every gesture in the bar carries, so all of them take the same focus ring. */
const ACTION_CLASS =
  "rounded border border-border px-2.5 py-1.5 text-sm text-foreground hover:text-accent focus:outline-none focus:ring-2 focus:ring-accent";

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
        <div className="flex flex-wrap items-center gap-2 rounded border border-border bg-card px-3 py-2">
          <span className="text-sm text-foreground">{selectionCount(selected.size)}</span>
          {actions.map((action) => (
            <button
              key={action.key}
              type="button"
              className={ACTION_CLASS}
              onClick={() => {
                onSelect(action.resulting);
              }}
            >
              {action.label}
            </button>
          ))}
          <OptionallyDisabled
            name={MONITOR_SELECTION_LABEL}
            variant="primary"
            reason={outcome.kind === "inFlight" ? WAITING_FOR_WHISPARR : null}
            onClick={onMonitorSelection}
          />
          <span className="text-xs text-secondary">{BULK_REPORTS_IN_THE_JOB_DRAWER}</span>
        </div>
      )}
      <p role="status" aria-live="polite" className="mt-1 text-sm text-muted">
        {refusal}
      </p>
    </div>
  );
}

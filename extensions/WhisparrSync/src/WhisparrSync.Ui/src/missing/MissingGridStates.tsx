/**
 * The sentence the grid states when it cannot show cards, and the one it states above them.
 *
 * The reason is rendered once rather than on every card, so `RefusalNotice` takes a count of the
 * controls it covers instead of being mounted per card.
 */
import {
  ACTION_REFRESH,
  ADDING_TO_WHISPARR,
  ADD_TO_WHISPARR,
  ADD_TO_WHISPARR_TRACKS_ONLY,
} from "../common/ui/copy";
import { RefusalNotice } from "../common/ui/RefusalNotice";
import { describeGridState, type MissingGridStateKind } from "./missingStatesLogic";

export interface MissingGridStateActions {
  readonly onRefresh: (() => void) | null;
  readonly onClearFilters: (() => void) | null;
  readonly onClearSearch: (() => void) | null;
  /** Adds the entity so Whisparr lists its scenes. Null where the caller offers no add. */
  readonly onAdd: (() => void) | null;
  /** The add is in flight, so the control says so and is not pressed twice. */
  readonly addInFlight: boolean;
  /** What the add answered, or null while it has answered nothing. */
  readonly addRefusal: string | null;
}

const CLEAR_FILTERS = "Clear filters";
const CLEAR_SEARCH = "Clear search";

const CONTROL_CLASS =
  "rounded border border-border px-2.5 py-1.5 text-sm text-foreground hover:text-accent focus:outline-none focus:ring-2 focus:ring-accent";

export function MissingGridStates({
  kind,
  sentence,
  affectedControls,
  actions,
}: {
  kind: MissingGridStateKind;
  /** The kind's own sentence with the provider and entity names filled in. */
  sentence: string;
  /** How many controls on screen the reason applies to, so a reason covering none renders nothing. */
  affectedControls: number;
  actions: MissingGridStateActions;
}) {
  const state = describeGridState(kind);

  if (!state.replacesTheGrid) {
    return <RefusalNotice reason={sentence} affectedControls={affectedControls} />;
  }

  return (
    <div className="mt-8 flex flex-col items-center justify-center gap-3 p-8 text-center">
      <p className="text-sm text-muted">{sentence}</p>
      {state.addIsOffered ? (
        <p className="max-w-prose text-xs text-muted">{ADD_TO_WHISPARR_TRACKS_ONLY}</p>
      ) : null}
      {actions.addRefusal === null ? null : (
        <p role="status" aria-live="polite" className="text-xs text-red-400">
          {actions.addRefusal}
        </p>
      )}
      <div className="flex items-center gap-2">
        {state.clearFiltersIsOffered && actions.onClearFilters !== null ? (
          <button type="button" onClick={actions.onClearFilters} className={CONTROL_CLASS}>
            {CLEAR_FILTERS}
          </button>
        ) : null}
        {state.clearSearchIsOffered && actions.onClearSearch !== null ? (
          <button type="button" onClick={actions.onClearSearch} className={CONTROL_CLASS}>
            {CLEAR_SEARCH}
          </button>
        ) : null}
        {state.addIsOffered && actions.onAdd !== null ? (
          <button
            type="button"
            onClick={actions.onAdd}
            disabled={actions.addInFlight}
            className={`${CONTROL_CLASS} disabled:opacity-60`}
          >
            {actions.addInFlight ? ADDING_TO_WHISPARR : ADD_TO_WHISPARR}
          </button>
        ) : null}
        {state.refreshIsOffered && actions.onRefresh !== null ? (
          <button type="button" onClick={actions.onRefresh} className={CONTROL_CLASS}>
            {ACTION_REFRESH}
          </button>
        ) : null}
      </div>
    </div>
  );
}

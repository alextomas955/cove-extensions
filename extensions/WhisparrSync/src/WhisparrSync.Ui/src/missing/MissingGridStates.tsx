/**
 * The sentence the grid states when it cannot show cards, and the one it states above them.
 *
 * The reason is taken once and rendered once. A notice repeated on forty cards consumes a quarter of
 * every card and teaches the reader to skip it, which is why `RefusalNotice` takes a count of the
 * controls it covers rather than being mounted per card.
 */
import { ACTION_REFRESH } from "../common/ui/copy";
import { RefusalNotice } from "../common/ui/RefusalNotice";
import { describeGridState, type MissingGridStateKind } from "./missingStatesLogic";

/** The escapes and the retry an empty grid can offer. */
export interface MissingGridStateActions {
  readonly onRefresh: (() => void) | null;
  readonly onClearFilters: (() => void) | null;
  readonly onClearSearch: (() => void) | null;
}

/** The escape labels, which the empty states are the only surface to render. */
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
        {state.refreshIsOffered && actions.onRefresh !== null ? (
          <button type="button" onClick={actions.onRefresh} className={CONTROL_CLASS}>
            {ACTION_REFRESH}
          </button>
        ) : null}
      </div>
    </div>
  );
}

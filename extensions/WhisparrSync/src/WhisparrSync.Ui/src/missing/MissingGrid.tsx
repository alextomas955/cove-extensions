/**
 * The card grid and the count line above it. The first read draws a spinner and every later one
 * keeps the previous page on screen, so the grid never blanks between pages.
 */
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { deriveAsyncRegionState, type AsyncRead } from "../common/ui/asyncRegionLogic";
import type { MissingPageView } from "../wire/api";
import type { MultiSelectToggleHandler } from "@cove/runtime/components";
import { MissingCard } from "./MissingCard";
import type { CardActionState } from "./missingCardLogic";
import { MissingGridStates } from "./MissingGridStates";
import { GRID_CLASS, GRID_TEMPLATE_COLUMNS } from "./missingClasses";
import {
  deriveGridState,
  describeGridState,
  emptyStateFor,
  fillNames,
  type MissingGridStateKind,
} from "./missingStatesLogic";

interface MissingGridAdd {
  /** Adds the entity so Whisparr lists its scenes, or null where no add is offered. */
  readonly onAdd: (() => void) | null;
  readonly inFlight: boolean;
  /** What the last add answered, or null while it has answered nothing. */
  readonly refusal: string | null;
}

export interface MissingGridSurroundings {
  readonly provider: string;
  readonly entityName: string;
  readonly filtersActive: boolean;
  readonly searchActive: boolean;
  /** A studio read without the scenes the provider attributes to its sub-studios. */
  readonly subStudioContentIsExcluded: boolean;
  readonly onRefresh: (() => void) | null;
  readonly onClearFilters: (() => void) | null;
  readonly onClearSearch: (() => void) | null;
  readonly add: MissingGridAdd;
}

export interface MissingGridCards {
  readonly selected: ReadonlySet<string>;
  /** A selection is in progress, so every card shows its control rather than only the hovered one. */
  readonly selecting: boolean;
  readonly onToggleSelect: MultiSelectToggleHandler<string>;
  /** Keyed as the provider issued the scene identifier. */
  readonly actions: Readonly<Record<string, CardActionState>>;
  readonly onMonitor: (providerSceneId: string) => void;
  readonly onSearch: (providerSceneId: string) => void;
}

// The reasons no retry and no change of view could answer.
const NEVER_ANSWERS: readonly MissingGridStateKind[] = [
  "noProviderIdForEntity",
  "noMetadataProviderConfigured",
  "noInstanceConnected",
];

export function MissingGrid({
  read,
  view,
  surroundings,
  cards: wiring,
}: {
  read: AsyncRead;
  view: MissingPageView | null;
  surroundings: MissingGridSurroundings;
  cards: MissingGridCards;
}) {
  const cards = view?.cards ?? [];
  const kind = deriveGridState({ read, view, ...surroundings });
  const state = kind === null ? null : describeGridState(kind);

  const stated =
    kind === null ? null : (
      <MissingGridStates
        kind={kind}
        sentence={fillNames(emptyStateFor(kind), surroundings.provider, surroundings.entityName)}
        // One notice for the whole page, never one per card.
        affectedControls={state?.replacesTheGrid === false ? Math.max(1, cards.length) : 1}
        actions={{
          onRefresh: surroundings.onRefresh,
          onClearFilters: surroundings.onClearFilters,
          onClearSearch: surroundings.onClearSearch,
          onAdd: surroundings.add.onAdd,
          addInFlight: surroundings.add.inFlight,
          addRefusal: surroundings.add.refusal,
        }}
      />
    );

  // A surface with no possible answer is omitted, not given an empty state that would read as a
  // factual zero.
  const canAnswer = kind === null || !NEVER_ANSWERS.includes(kind);
  const replacesTheGrid = state?.replacesTheGrid === true;
  // The region carries the reason in its own empty, failed or outage slot where it has one. The
  // rest are stated above it, as is every reason whose region is omitted.
  const statedAboveTheRegion =
    stated !== null && (!canAnswer || (!replacesTheGrid && kind !== "readIsStale"));

  return (
    <>
      {statedAboveTheRegion ? stated : null}
      <AsyncRegion
        available={canAnswer}
        state={deriveAsyncRegionState(
          // A page that answered with no card counts as empty, not as content, so the reason is
          // stated instead of an empty grid.
          { ...read, hasContent: read.hasContent && cards.length > 0 },
        )}
        outageNotice={kind === "readIsStale" ? stated : null}
        content={
          <>
            <div className={GRID_CLASS} style={{ gridTemplateColumns: GRID_TEMPLATE_COLUMNS }}>
              {cards.map((card) => (
                <MissingCard
                  key={card.providerSceneId}
                  card={card}
                  selected={wiring.selected.has(card.providerSceneId)}
                  selecting={wiring.selecting}
                  onToggleSelect={(options) => {
                    wiring.onToggleSelect(card.providerSceneId, options);
                  }}
                  action={wiring.actions[card.providerSceneId]}
                  onMonitor={wiring.onMonitor}
                  onSearch={wiring.onSearch}
                />
              ))}
            </div>
          </>
        }
        empty={stated}
        failed={stated}
      />
    </>
  );
}

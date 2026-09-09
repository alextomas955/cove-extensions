/**
 * The card grid, the count line above it, and the four-way read split around both.
 *
 * The first read draws a spinner and every later one keeps the previous page on screen, so the grid
 * never blanks between pages.
 */
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { deriveAsyncRegionState, type AsyncRead } from "../common/ui/asyncRegionLogic";
import type { MissingPageView } from "../wire/api";
import type { MultiSelectToggleHandler } from "@cove/runtime/components";
import { MissingCard } from "./MissingCard";
import type { CardActionState } from "./missingCardLogic";
import { MissingCountLine } from "./MissingCountLine";
import { MissingGridStates } from "./MissingGridStates";
import { GRID_CLASS, GRID_TEMPLATE_COLUMNS } from "./missingClasses";
import {
  deriveGridState,
  describeGridState,
  emptyStateFor,
  fillNames,
  type MissingGridStateKind,
} from "./missingStatesLogic";

/** What the grid needs to state its own reason and to say what the figure beside it counts. */
export interface MissingGridSurroundings {
  /** The metadata source the catalogue was read from, as a sentence names it. */
  readonly provider: string;
  /** The entity the catalogue belongs to, as a sentence names it. */
  readonly entityName: string;
  readonly filtersActive: boolean;
  readonly searchActive: boolean;
  /** A studio read without the scenes the provider attributes to its sub-studios. */
  readonly subStudioContentIsExcluded: boolean;
  readonly onRefresh: (() => void) | null;
  readonly onClearFilters: (() => void) | null;
  readonly onClearSearch: (() => void) | null;
}

/** What each card is given, held at the tab beside the page the selection is a selection of. */
export interface MissingGridCards {
  readonly selected: ReadonlySet<string>;
  /** A selection is in progress, so every card shows its control rather than only the hovered one. */
  readonly selecting: boolean;
  readonly onToggleSelect: MultiSelectToggleHandler<string>;
  /** What each card's own verbs are doing, keyed as the provider issued the scene identifier. */
  readonly actions: Readonly<Record<string, CardActionState>>;
  readonly onMonitor: (providerSceneId: string) => void;
  readonly onSearch: (providerSceneId: string) => void;
}

/** The reasons no retry and no change of view could answer. */
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
  /** What the grid states its own reason from, and what the count line says it counts. */
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
        // One notice for the whole page, never one per card, and none at all where it covers nothing.
        affectedControls={state?.replacesTheGrid === false ? Math.max(1, cards.length) : 1}
        actions={{
          onRefresh: surroundings.onRefresh,
          onClearFilters: surroundings.onClearFilters,
          onClearSearch: surroundings.onClearSearch,
        }}
      />
    );

  // A surface with no possible answer is omitted rather than given an empty state, an empty state
  // there reading as a factual zero.
  const canAnswer = kind === null || !NEVER_ANSWERS.includes(kind);
  const replacesTheGrid = state?.replacesTheGrid === true;
  // The region carries the reason in its own empty, failed or outage slot wherever it has one; the
  // rest are stated above it, and so is every reason whose region is omitted.
  const statedAboveTheRegion =
    stated !== null && (!canAnswer || (!replacesTheGrid && kind !== "readIsStale"));

  return (
    <>
      {statedAboveTheRegion ? stated : null}
      <AsyncRegion
        available={canAnswer}
        state={deriveAsyncRegionState(
          // A page that answered with no card is an EMPTY answer rather than content, so the reason
          // is stated instead of an empty grid being drawn.
          { ...read, hasContent: read.hasContent && cards.length > 0 },
        )}
        outageNotice={kind === "readIsStale" ? stated : null}
        content={
          <>
            {view === null ? null : (
              <MissingCountLine
                provider={surroundings.provider}
                entityName={surroundings.entityName}
              />
            )}
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

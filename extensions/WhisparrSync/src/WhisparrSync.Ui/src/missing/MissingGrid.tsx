/**
 * The card grid, the count line above it, and the four-way read split around both.
 *
 * The first read draws a spinner and every later one keeps the previous page on screen, so the grid
 * never blanks between pages.
 */
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { deriveAsyncRegionState, type AsyncRead } from "../common/ui/asyncRegionLogic";
import type { MissingPageView } from "../wire/api";
import { MissingCard } from "./MissingCard";
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

/** The reasons no retry and no change of view could answer. */
const NEVER_ANSWERS: readonly MissingGridStateKind[] = [
  "noProviderIdForEntity",
  "noMetadataProviderConfigured",
  "noInstanceConnected",
];

export function MissingGrid({
  read,
  view,
  empty,
  failed,
  surroundings = null,
}: {
  read: AsyncRead;
  view: MissingPageView | null;
  /** What is stated when the read answered and the page carries no card. */
  empty: React.ReactNode;
  /** What is stated when the read did not answer. */
  failed: React.ReactNode;
  /**
   * What the grid states its own reason from. Null leaves the reason to the caller's own `empty` and
   * `failed`, and draws no count line, there being no provider or entity name to say what it counts.
   */
  surroundings?: MissingGridSurroundings | null;
}) {
  const cards = view?.cards ?? [];
  const kind = surroundings === null ? null : deriveGridState({ read, view, ...surroundings });
  const state = kind === null ? null : describeGridState(kind);

  const stated =
    kind === null || surroundings === null ? null : (
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
          <div className={GRID_CLASS} style={{ gridTemplateColumns: GRID_TEMPLATE_COLUMNS }}>
            {cards.map((card) => (
              <MissingCard key={card.providerSceneId} card={card} />
            ))}
          </div>
        }
        empty={replacesTheGrid ? stated : empty}
        failed={replacesTheGrid ? stated : failed}
      />
    </>
  );
}

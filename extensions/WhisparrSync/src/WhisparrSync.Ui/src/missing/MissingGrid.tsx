/**
 * The card grid, and the four-way read split above it.
 *
 * The first read draws a spinner and every later one keeps the previous page on screen, so the grid
 * never blanks between pages.
 */
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { deriveAsyncRegionState, type AsyncRead } from "../common/ui/asyncRegionLogic";
import type { MissingPageView } from "../wire/api";
import { MissingCard } from "./MissingCard";
import { GRID_CLASS, GRID_TEMPLATE_COLUMNS } from "./missingClasses";

export function MissingGrid({
  read,
  view,
  empty,
  failed,
}: {
  read: AsyncRead;
  view: MissingPageView | null;
  /** What is stated when the read answered and the page carries no card. */
  empty: React.ReactNode;
  /** What is stated when the read did not answer. */
  failed: React.ReactNode;
}) {
  const cards = view?.cards ?? [];
  return (
    <AsyncRegion
      state={deriveAsyncRegionState(
        // A page that answered with no card is an EMPTY answer rather than content, so the reason
        // is stated instead of an empty grid being drawn.
        { ...read, hasContent: read.hasContent && cards.length > 0 },
      )}
      content={
        <div className={GRID_CLASS} style={{ gridTemplateColumns: GRID_TEMPLATE_COLUMNS }}>
          {cards.map((card) => (
            <MissingCard key={card.providerSceneId} card={card} />
          ))}
        </div>
      }
      empty={empty}
      failed={failed}
    />
  );
}

/**
 * The range this page covers and what the figure beside it counts.
 *
 * A live region, because a facet or a search changes the figure without moving focus: a sighted
 * reader sees it move and a screen-reader user is told.
 */
import { countLine } from "../common/ui/copy";
import type { MissingPageView } from "../wire/api";
import { catalogueSizeLabel, countLineParts } from "./missingCountLogic";

export function MissingCountLine({
  view,
  provider,
  entityName,
}: {
  view: MissingPageView;
  /** The metadata source the catalogue was read from, as a sentence names it. */
  provider: string;
  /** The entity the catalogue belongs to, as a sentence names it. */
  entityName: string;
}) {
  const parts = countLineParts(view);

  return (
    <p role="status" aria-live="polite" className="mb-3 text-xs text-muted">
      {countLine(parts.from, parts.to, parts.total, parts.atCeiling)}{" "}
      {catalogueSizeLabel(provider, entityName)}
    </p>
  );
}

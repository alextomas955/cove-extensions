/**
 * What the total in the toolbar counts.
 *
 * A live region, because a facet or a search can change which source and which entity the sentence
 * names without moving focus. The total itself is stated in the toolbar and is a live region there.
 */
import { catalogueSizeLabel } from "./missingCountLogic";

export function MissingCountLine({
  provider,
  entityName,
}: {
  /** The metadata source the catalogue was read from, as a sentence names it. */
  provider: string;
  /** The entity the catalogue belongs to, as a sentence names it. */
  entityName: string;
}) {
  return (
    <p role="status" aria-live="polite" className="mb-3 text-xs text-muted">
      {catalogueSizeLabel(provider, entityName)}
    </p>
  );
}

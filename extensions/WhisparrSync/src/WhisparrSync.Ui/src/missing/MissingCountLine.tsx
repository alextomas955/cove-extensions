/**
 * What the total in the toolbar counts.
 *
 * A live region, because a facet or a search can change which source and which entity the sentence
 * names without moving focus.
 */
import { catalogueSizeLabel } from "./missingCountLogic";

export function MissingCountLine({
  provider,
  entityName,
}: {
  provider: string;
  entityName: string;
}) {
  return (
    <p role="status" aria-live="polite" className="mb-3 text-xs text-muted">
      {catalogueSizeLabel(provider, entityName)}
    </p>
  );
}

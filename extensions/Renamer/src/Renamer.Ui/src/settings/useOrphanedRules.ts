/**
 * Which routing-rule keys name an entity Cove no longer holds.
 *
 * Answered by the extension's own route, which asks the database as System. The browser cannot
 * decide this for itself: a failed lookup reads the same for a deleted entity, for one this viewer
 * may not read, and for a dropped request.
 *
 * A failed read reports nothing orphaned, so the panel falls back to the host's own label rather
 * than accusing every rule of being broken.
 */
import { useQuery } from "@tanstack/react-query";

import { renamerGetOrphanedRulesOptions } from "../wire";

/** Rule keys whose entity is gone, as sets for a per-row lookup. */
interface OrphanedRules {
  studios: ReadonlySet<number>;
  tags: ReadonlySet<number>;
}

const NONE: OrphanedRules = { studios: new Set(), tags: new Set() };

export function useOrphanedRules(): OrphanedRules {
  // The sets are built in `select` rather than on each render, so a row's lookup compares against the
  // same object until the response itself changes.
  const query = useQuery({
    ...renamerGetOrphanedRulesOptions(),
    select: (view): OrphanedRules => ({
      studios: new Set(view.studios),
      tags: new Set(view.tags),
    }),
  });

  return query.data ?? NONE;
}

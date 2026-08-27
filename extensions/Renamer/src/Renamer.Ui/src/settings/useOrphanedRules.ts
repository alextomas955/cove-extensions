/**
 * Which routing-rule keys name an entity Cove no longer holds (INFRA: HTTP).
 *
 * Answered by the extension's own route, which asks the database as System. The browser cannot decide
 * this for itself: a lookup that fails reads the same for a deleted entity, for one this viewer may not
 * read, and for a dropped request — and labelling a valid rule "deleted" on that confusion would be
 * worse than the stuck spinner it replaces.
 *
 * A failed read reports NOTHING orphaned, so the panel falls back to the host's own label rather than
 * accusing every rule of being broken.
 */
import { useEffect, useState } from "react";

import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import { api } from "../common/lib/extension";
import type { OrphanedRulesView } from "../wire/api";

const ORPHANED_RULES_PATH = api("orphaned-rules");

/** Rule keys whose entity is gone, as sets for a per-row lookup. */
export interface OrphanedRules {
  studios: ReadonlySet<number>;
  tags: ReadonlySet<number>;
}

const NONE: OrphanedRules = { studios: new Set(), tags: new Set() };

export function useOrphanedRules(): OrphanedRules {
  const [orphaned, setOrphaned] = useState<OrphanedRules>(NONE);

  useEffect(() => {
    let live = true;
    void requestJson<OrphanedRulesView>(ORPHANED_RULES_PATH)
      .then((view) => {
        if (live) setOrphaned({ studios: new Set(view.studios), tags: new Set(view.tags) });
      })
      .catch(() => {
        if (live) setOrphaned(NONE);
      });
    return () => {
      live = false;
    };
  }, []);

  return orphaned;
}

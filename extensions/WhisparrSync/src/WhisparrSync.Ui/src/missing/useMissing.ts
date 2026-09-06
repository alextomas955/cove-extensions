/**
 * One entity's catalogue data layer: the only place that reads a page of what is missing.
 *
 * Reading, answered and failed stay distinct all the way through, so a tab that is still reading
 * never paints the empty state it does not have an answer for yet.
 */
import { useCallback, useEffect, useState, useSyncExternalStore } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { MissingPageView } from "../wire/api";
import { api } from "../common/lib/extension";
import type { MissingEntityKind } from "./entityKindLogic";
import type { MissingView } from "./missingUrlLogic";
import {
  createMissingStore,
  type MissingEntity,
  type MissingState,
  type MissingStore,
  type MissingViewKey,
} from "./missingStore";

/** What the hook hands the tab. */
export interface Missing {
  readonly state: MissingState;
  /** Reads the current view again, keeping whatever is on screen while it runs. */
  readonly refresh: () => void;
}

/**
 * The filter map as one comparable string.
 *
 * The settle guard compares views by value, and an object identity would make every render a
 * different view and re-read the page on each one.
 */
function filterKey(filters: Readonly<Record<string, string>>): string {
  return Object.entries(filters)
    .sort(([left], [right]) => (left < right ? -1 : left > right ? 1 : 0))
    .map(([key, value]) => `${encodeURIComponent(key)}:${encodeURIComponent(value)}`)
    .join(",");
}

function keyOf(view: MissingView): MissingViewKey {
  return { page: view.page, sort: view.sort, q: view.q, filters: filterKey(view.filters) };
}

function routeFor(entity: MissingEntity, key: MissingViewKey): string {
  const query = new URLSearchParams({ page: String(key.page) });
  if (key.sort !== null) query.set("sort", key.sort);
  if (key.q !== "") query.set("q", key.q);
  if (key.filters !== "") query.set("filters", key.filters);
  return api(`entity/${entity.kind}/${String(entity.coveId)}/missing?${query.toString()}`);
}

export function useMissing(kind: MissingEntityKind, coveId: number, view: MissingView): Missing {
  // One store per page lifetime. A lazy useState initializer rather than a useMemo, because a memo
  // is a cache React may legitimately discard.
  const [store] = useState<MissingStore>(() => createMissingStore());
  const state = useSyncExternalStore(store.subscribe, store.getSnapshot);

  const key = keyOf(view);
  const { page, sort, q, filters } = key;

  const read = useCallback(
    (entity: MissingEntity, wanted: MissingViewKey) => {
      store.beginRead(entity, wanted);
      requestJson<MissingPageView>(routeFor(entity, wanted))
        .then((answered) => {
          store.loaded(entity, wanted, answered);
        })
        .catch(() => {
          store.readFailed(entity, wanted);
        });
    },
    [store],
  );

  useEffect(() => {
    const entity: MissingEntity = { kind, coveId };
    store.mounted(entity);
    read(entity, { page, sort, q, filters });
  }, [store, read, kind, coveId, page, sort, q, filters]);

  const refresh = useCallback(() => {
    read({ kind, coveId }, { page, sort, q, filters });
  }, [read, kind, coveId, page, sort, q, filters]);

  return { state, refresh };
}

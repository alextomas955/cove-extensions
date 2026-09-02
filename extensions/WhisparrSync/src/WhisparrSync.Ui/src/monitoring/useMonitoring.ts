/**
 * One entity's monitoring data layer: the only place that reads or changes what Whisparr monitors.
 *
 * Loading, answered and failed stay distinct all the way through, because a control that is still
 * reading must never paint the state it does not have yet.
 */
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { ApiError, requestJson } from "@cove-extensions/ui-shared/extensionRequest";

import type { EntityMonitoringView, MonitorScope, WhisparrEntityKind } from "../wire/api";
import { api } from "../common/lib/extension";
import {
  createMonitoringStore,
  type MonitoredEntity,
  type MonitoringState,
  type MonitoringStore,
} from "./monitoringStore";

/** What the hook hands the control. */
export interface Monitoring {
  readonly state: MonitoringState;
  /** Monitors the entity at `scope`, then reports the state the server answered with. */
  readonly monitor: (scope: MonitorScope) => void;
}

function messageFor(err: unknown): string {
  return err instanceof ApiError ? `${String(err.status)} ${err.body}` : String(err);
}

/** The route for one entity. Per entity, so it cannot be a module-scope constant. */
function routeFor(entity: MonitoredEntity, verb: string): string {
  return api(`entity/${entity.kind}/${String(entity.coveId)}/${verb}`);
}

export function useMonitoring(kind: WhisparrEntityKind, coveId: number): Monitoring {
  // One store per page lifetime. A lazy useState initializer rather than a useMemo, because a memo is
  // a cache React may legitimately discard.
  const [store] = useState<MonitoringStore>(() => createMonitoringStore());
  const state = useSyncExternalStore(store.subscribe, store.getSnapshot);

  const read = useCallback(
    (entity: MonitoredEntity) => {
      store.beginRead(entity);
      requestJson<EntityMonitoringView>(routeFor(entity, "monitoring"))
        .then((view) => {
          store.loaded(entity, view);
        })
        .catch((err: unknown) => {
          store.readFailed(entity, messageFor(err));
        });
    },
    [store],
  );

  const monitor = useCallback(
    (scope: MonitorScope) => {
      const entity: MonitoredEntity = { kind, coveId };
      store.beginAction(entity);
      requestJson<EntityMonitoringView>(routeFor(entity, "monitor"), {
        method: "POST",
        body: JSON.stringify({ scope }),
      })
        .then((view) => {
          store.acted(entity, view);
        })
        .catch((err: unknown) => {
          store.actionFailed(entity, messageFor(err));
        });
    },
    [store, kind, coveId],
  );

  // Keyed on the entity rather than a bare boolean. The host keeps this component across a
  // navigation between two entity pages, so a bare boolean would suppress the second entity's read
  // and leave its control blank for the whole visit.
  const primed = useRef<string | null>(null);
  useEffect(() => {
    const entity: MonitoredEntity = { kind, coveId };
    const key = `${kind}:${String(coveId)}`;
    store.mounted(entity);
    if (primed.current === key) return;
    primed.current = key;
    read(entity);
  }, [store, read, kind, coveId]);

  return { state, monitor };
}

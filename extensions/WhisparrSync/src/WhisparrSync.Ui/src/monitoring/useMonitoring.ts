/**
 * One entity's monitoring data layer: the only place that reads or changes what Whisparr monitors.
 *
 * Loading, answered and failed stay distinct all the way through, because a control that is still
 * reading must never paint the state it does not have yet.
 */
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { ApiError, requestJson } from "@cove-extensions/ui-shared/extensionRequest";
import { postAction } from "@cove-extensions/ui-shared/postAction";

import type { EntityMonitoringView, ReflectOwnedEnqueued, WhisparrEntityKind } from "../wire/api";
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
  /**
   * Carries out one verb for this entity and then reads the state back.
   *
   * @param verb the route this verb is served at, off the entity's own base
   * @param body what that route is sent
   */
  readonly act: (verb: string, body: unknown) => void;
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

  const act = useCallback(
    (verb: string, body: unknown) => {
      const entity: MonitoredEntity = { kind, coveId };
      store.beginAction(entity);
      postAction<Partial<ReflectOwnedEnqueued>>(routeFor(entity, verb), body)
        .then((answered) => {
          // The answer's skip reason is the ONE thing read off it. A verb that was carried out is
          // read back from the instance instead, because it decides what it now holds and its own
          // catalogue refresh can move that between the answer and the next frame.
          const skipped = answered.skipped ?? null;
          if (skipped === null) {
            store.actionSucceeded(entity);
          } else {
            store.actionSkipped(entity, skipped);
          }
          read(entity);
        })
        .catch((err: unknown) => {
          store.actionFailed(entity, messageFor(err));
        });
    },
    [store, read, kind, coveId],
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

  return { state, act };
}

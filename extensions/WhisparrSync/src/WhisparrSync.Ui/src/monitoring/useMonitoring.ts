/**
 * One entity's monitoring data layer: the only place that reads or changes what Whisparr monitors.
 *
 * Loading, answered and failed stay distinct all the way through, because a control that is still
 * reading must never paint the state it does not have yet.
 */
import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { requestJson } from "@cove-extensions/ui-shared/extensionRequest";
import { postAction } from "@cove-extensions/ui-shared/postAction";

import type { EntityMonitoringView, WhisparrEntityKind } from "../wire/api";
import {
  announceEntityChanged,
  isTheSameEntity,
  onEntityChanged,
} from "../common/lib/entityChanged";
import { api } from "../common/lib/extension";
import {
  monitorRefusalIn,
  reflectOwnedSkipIn,
  type MonitorActionAnswer,
  type MonitorActionRoute,
} from "./monitorMenuLogic";
import {
  createMonitoringStore,
  type MonitoredEntity,
  type MonitoringState,
  type MonitoringStore,
} from "./monitoringStore";

export interface Monitoring {
  readonly state: MonitoringState;
  /**
   * Carries out one verb for this entity and then reads the state back.
   *
   * @param verb the route this verb is served at, off the entity's own base
   * @param body what that route is sent
   */
  readonly act: (verb: MonitorActionRoute, body: unknown) => void;
}

function routeFor(entity: MonitoredEntity, verb: string): string {
  return api(`entity/${entity.kind}/${String(entity.coveId)}/${verb}`);
}

export function useMonitoring(kind: WhisparrEntityKind, coveId: number): Monitoring {
  // One store per page lifetime. A lazy useState initializer rather than a useMemo, because a memo
  // is a cache React may discard.
  const [store] = useState<MonitoringStore>(() => createMonitoringStore());
  const state = useSyncExternalStore(store.subscribe, store.getSnapshot);

  const read = useCallback(
    (entity: MonitoredEntity) => {
      store.beginRead(entity);
      requestJson<EntityMonitoringView>(routeFor(entity, "monitoring"))
        .then((view) => {
          store.loaded(entity, view);
        })
        .catch(() => {
          store.readFailed(entity);
        });
    },
    [store],
  );

  const act = useCallback(
    (verb: MonitorActionRoute, body: unknown) => {
      const entity: MonitoredEntity = { kind, coveId };
      store.beginAction(entity);
      postAction<MonitorActionAnswer>(routeFor(entity, verb), body)
        .then((answered) => {
          // A refusal is read off the press rather than off the read that follows it. The read
          // route composes no add, so it can never answer either add-defaults kind, and its fresh
          // view overwrites this one. The state is still read back, because the instance decides
          // what it now holds.
          const refusal = monitorRefusalIn(answered);
          const skipped = reflectOwnedSkipIn(answered);
          if (refusal !== null && refusal !== "none") {
            store.actionRefused(entity, refusal);
          } else if (skipped !== null) {
            store.actionSkipped(entity, skipped);
          } else {
            store.actionSucceeded(entity);
          }
          // Announced rather than read here: this control subscribes to the same entity, so the
          // read happens once and the tab beside it, which lists what the reader does not own, is
          // told at the same moment. Adding every missing scene empties that tab, and a scope
          // change moves what it marks.
          announceEntityChanged(entity);
        })
        .catch(() => {
          store.actionFailed(entity);
        });
    },
    [store, kind, coveId],
  );

  // Read again whenever something else on this page acted on the same entity: the tab beside this
  // control can add the entity to the instance, which is exactly what this control reports on.
  useEffect(
    () =>
      onEntityChanged((announced) => {
        if (isTheSameEntity(announced, { kind, coveId })) read({ kind, coveId });
      }),
    [read, kind, coveId],
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

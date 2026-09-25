/**
 * The catalogue tab, registered once and mounted on all three entity page types.
 *
 * The host passes an entity id and a navigate callback and nothing else, so the kind is read from
 * the address. A route this tab does not recognise states that rather than guessing a kind.
 *
 * Every visual is a host-emitted utility class, because an extension CSS bundle is page-global
 * and would leak onto every host page.
 */
import { useCallback, useEffect, useMemo, useState } from "react";

import { NO_PROVIDER_ID_FOR_ENTITY, THE_METADATA_SOURCE } from "../common/ui/copy";
import { trackRefusal } from "./missingTrackLogic";
import { fillNames } from "./missingStatesLogic";
import type { WhisparrEntityKind } from "../wire/api";
import { readEntityKind } from "./entityKindLogic";
import { useMultiSelect } from "./hostComponents";
import { MissingGrid } from "./MissingGrid";
import { runIsUnderWay } from "./missingRunLogic";
import { MissingPager } from "./MissingPager";
import { MissingSelectionBar } from "./MissingSelectionBar";
import { MissingToolbar } from "./MissingToolbar";
import { useMissing } from "./useMissing";
import { useMissingUrlState } from "./useMissingUrlState";

// The host's own sub-studio toggle, which lives in the address and is operable beside this tab.
const INCLUDE_SUB_STUDIOS_KEY = "includeSubStudios";

// The host event fired when its own router changes the address.
const HOST_LOCATION_CHANGE = "cove-locationchange";

// The toggle is not one of the keys the host deletes on a tab change, and it renders above this
// tab while it is open, so its value is followed rather than read once at mount.
function useIncludeSubStudios(): boolean {
  const [included, setIncluded] = useState(
    () => new URLSearchParams(window.location.search).get(INCLUDE_SUB_STUDIOS_KEY) === "true",
  );

  useEffect(() => {
    const fromTheAddress = () => {
      setIncluded(
        new URLSearchParams(window.location.search).get(INCLUDE_SUB_STUDIOS_KEY) === "true",
      );
    };
    window.addEventListener("popstate", fromTheAddress);
    window.addEventListener(HOST_LOCATION_CHANGE, fromTheAddress);
    return () => {
      window.removeEventListener("popstate", fromTheAddress);
      window.removeEventListener(HOST_LOCATION_CHANGE, fromTheAddress);
    };
  }, []);

  return included;
}

export function WhisparrMissingTab({ entityId }: Readonly<{ entityId: number }>) {
  const kind = readEntityKind(window.location.pathname);
  const includeSubStudios = useIncludeSubStudios();

  if (kind === null) {
    return <p className="text-sm text-muted">{NO_PROVIDER_ID_FOR_ENTITY}</p>;
  }

  return <MissingTabFor kind={kind} coveId={entityId} includeSubStudios={includeSubStudios} />;
}

function MissingTabFor({
  kind,
  coveId,
  includeSubStudios,
}: Readonly<{
  kind: WhisparrEntityKind;
  coveId: number;
  includeSubStudios: boolean;
}>) {
  const [view, setView] = useMissingUrlState();

  // The toggle is the host's, so it is carried into the request and never written into this
  // tab's own keys.
  const filters =
    kind === "studio"
      ? { ...view.filters, [INCLUDE_SUB_STUDIOS_KEY]: String(includeSubStudios) }
      : view.filters;

  const {
    state,
    refresh,
    monitorScene,
    searchScene,
    monitorSelection,
    unmonitorSelection,
    monitorAll,
    trackEntity,
    track,
  } = useMissing(kind, coveId, { ...view, filters });
  const entityName = `this ${kind}`;
  const page = state.view;

  const loadedPageIds = useMemo(
    () => (page?.cards ?? []).map((card) => card.providerSceneId),
    [page],
  );
  const items = useMemo(() => loadedPageIds.map((id) => ({ id })), [loadedPageIds]);

  // At the hook's preserve-on-items-change default of false, so the selection clears when the
  // page under it changes and a tick always means a scene on screen.
  const { selectedIds, toggle, selectIds, selectNone } = useMultiSelect(items);

  const onSelect = useCallback(
    (ids: readonly string[]) => {
      selectIds([...ids]);
    },
    [selectIds],
  );

  // The ticks are dropped once the run has finished, not when it starts: while it is under way the
  // cards it covers say so, and a reader who could not see which ones were picked would have no
  // idea what the run was working through.
  const runUnderWay = runIsUnderWay(state.running);
  const runFinished = !runUnderWay && state.bulk.kind === "atRest";
  useEffect(() => {
    if (runFinished) selectNone();
  }, [runFinished, selectNone]);

  return (
    <div className="mx-auto max-w-7xl px-4">
      <MissingToolbar
        onRefresh={refresh}
        onMonitorAll={monitorAll}
        catalogue={page === null ? undefined : { kind, view: page }}
      />
      <MissingSelectionBar
        loadedPageIds={loadedPageIds}
        selected={selectedIds}
        outcome={state.bulk}
        runUnderWay={runUnderWay}
        onSelect={onSelect}
        onUnmonitorSelection={() => {
          unmonitorSelection([...selectedIds]);
        }}
        onMonitorSelection={() => {
          monitorSelection([...selectedIds]);
        }}
      />
      <MissingGrid
        read={state.read}
        view={page}
        surroundings={{
          provider: page?.providerName ?? THE_METADATA_SOURCE,
          entityName,
          filtersActive: Object.keys(view.filters).length > 0,
          searchActive: view.q !== "",
          subStudioContentIsExcluded: kind === "studio" && !includeSubStudios,
          onRefresh: refresh,
          add: {
            onAdd: trackEntity,
            inFlight: track.kind === "inFlight",
            refusal:
              track.kind === "refused"
                ? fillNames(trackRefusal(track.outcome), THE_METADATA_SOURCE, entityName)
                : null,
          },
          onClearFilters: () => {
            setView({ ...view, filters: {} });
          },
          onClearSearch: () => {
            setView({ ...view, q: "" });
          },
        }}
        cards={{
          selected: selectedIds,
          selecting: selectedIds.size > 0,
          onToggleSelect: toggle,
          actions: state.cardActions,
          running: state.running,
          onMonitor: monitorScene,
          onSearch: searchScene,
        }}
      />
      {page === null ? null : (
        <div className="mt-4">
          <MissingPager
            page={page.page}
            perPage={page.perPage}
            lastPage={page.lastPage}
            onPage={(next) => {
              setView({ ...view, page: next });
            }}
          />
        </div>
      )}
    </div>
  );
}

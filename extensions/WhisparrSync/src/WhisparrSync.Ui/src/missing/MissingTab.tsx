/**
 * The catalogue tab, registered once and mounted on all three entity page types.
 *
 * The host passes an entity id and a navigate callback and nothing else, so the kind is read from
 * the address. A route this tab does not recognise states that rather than guessing a kind.
 *
 * No stylesheet and no background of its own: an extension CSS bundle is page-global and would leak
 * onto every host page, so every visual here is a host-emitted utility class.
 */
import { useEffect, useState } from "react";

import {
  NOTHING_MISSING,
  NO_METADATA_PROVIDER_CONFIGURED,
  NO_PROVIDER_ID_FOR_ENTITY,
  NO_SCENES_MATCH_THESE_FILTERS,
  NO_SCENES_WITHOUT_SUB_STUDIOS,
  PROVIDER_UNREACHABLE,
  WHISPARR_KEEPS_NO_SCENE_RECORDS,
  WHISPARR_STATUS_NOT_READ,
} from "../common/ui/copy";
import type { MissingRefusalKind } from "../wire/api";
import { readEntityKind, type MissingEntityKind } from "./entityKindLogic";
import { MissingGrid } from "./MissingGrid";
import { MissingPager } from "./MissingPager";
import { MissingToolbar } from "./MissingToolbar";
import { useMissing } from "./useMissing";
import { useMissingUrlState } from "./useMissingUrlState";

/** The host's own sub-studio toggle, which lives in the address and is operable beside this tab. */
const INCLUDE_SUB_STUDIOS_KEY = "includeSubStudios";

/** The host event fired when its own router changes the address. */
const HOST_LOCATION_CHANGE = "cove-locationchange";

/** The provider this build reads a catalogue from, named where a sentence asks for it. */
const PROVIDER_NAME = "StashDB";

/** The sentence stated above the grid for each refusal the server can answer. */
const GRID_REFUSAL: Partial<Record<MissingRefusalKind, string>> = {
  noMetadataProviderConfigured: NO_METADATA_PROVIDER_CONFIGURED,
  noProviderIdForEntity: NO_PROVIDER_ID_FOR_ENTITY,
  providerUnreachable: PROVIDER_UNREACHABLE,
  whisparrStatusNotRead: WHISPARR_STATUS_NOT_READ,
  whisparrKeepsNoSceneRecords: WHISPARR_KEEPS_NO_SCENE_RECORDS,
};

function fill(sentence: string, entity: string): string {
  return sentence.replace("{provider}", PROVIDER_NAME).replace("{entity}", entity);
}

/**
 * Whether the host's own sub-studio toggle is on, tracked live.
 *
 * The toggle is not one of the keys the host deletes on a tab change, and it renders above this tab
 * while it is open, so its value has to be followed rather than read once at mount.
 */
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

export function WhisparrMissingTab({ entityId }: { entityId: number }) {
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
}: {
  kind: MissingEntityKind;
  coveId: number;
  includeSubStudios: boolean;
}) {
  const [view, setView] = useMissingUrlState();

  // The toggle is the host's, so it is carried into the request rather than written into this tab's
  // own keys: writing it back would make this tab a second owner of a control it does not own.
  const filters =
    kind === "studio"
      ? { ...view.filters, [INCLUDE_SUB_STUDIOS_KEY]: String(includeSubStudios) }
      : view.filters;

  const { state, refresh } = useMissing(kind, coveId, { ...view, filters });
  const entityName = `this ${kind}`;
  const page = state.view;

  const refusal = page === null || page.refusal === "none" ? null : GRID_REFUSAL[page.refusal];

  return (
    <div className="mx-auto max-w-7xl px-4">
      <MissingToolbar onRefresh={refresh} />
      {refusal === undefined || refusal === null ? null : (
        <p className="mb-3 text-sm text-muted">{fill(refusal, entityName)}</p>
      )}
      <MissingGrid
        read={state.read}
        view={page}
        empty={
          <p className="text-sm text-muted">
            {fill(emptyReason(view.q, view.filters, kind, includeSubStudios), entityName)}
          </p>
        }
        failed={<p className="text-sm text-muted">{fill(PROVIDER_UNREACHABLE, entityName)}</p>}
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

/**
 * Why the grid is empty.
 *
 * A parent studio read without its sub-studios is held apart from owning everything: the provider
 * attributes those scenes one level down, so the catalogue really is empty while thousands of scenes
 * exist, and stating that the reader owns them all would be vacuously true of the query and false to
 * a reader.
 */
function emptyReason(
  q: string,
  filters: Readonly<Record<string, string>>,
  kind: MissingEntityKind,
  includeSubStudios: boolean,
): string {
  if (q !== "" || Object.keys(filters).length > 0) {
    return NO_SCENES_MATCH_THESE_FILTERS;
  }
  if (kind === "studio" && !includeSubStudios) {
    return NO_SCENES_WITHOUT_SUB_STUDIOS;
  }
  return NOTHING_MISSING;
}

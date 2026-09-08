/**
 * The scene tab, mounted on the video detail page.
 *
 * The host passes an entity id and nothing else, and the page type is fixed, so nothing here reads
 * the address.
 *
 * No outer padding and no max-width wrapper: the host already pads the tab panel it mounts this in.
 * No stylesheet and no background of its own either, because an extension CSS bundle is page-global
 * and would leak onto every host page, so every visual here is a host-emitted utility class.
 *
 * Every sentence below is declared in the copy module. This file composes none of its own.
 */
import { StatusText } from "@cove-extensions/ui-shared";

import type { SceneDetailView, SceneRefusalKind } from "../wire/api";
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import { RefusalNotice } from "../common/ui/RefusalNotice";
import { StateChip } from "../common/ui/StateChip";
import { deriveState } from "../common/ui/stateVocabularyLogic";
import {
  INSTANCE_REFUSED,
  NO_IDENTITY_IN_THIS_NAMESPACE,
  NO_INSTANCE_CONNECTED,
  SCENE_CUTOFF_NOT_NAMED,
  SCENE_FACT_CUTOFF,
  SCENE_FACT_PROFILE,
  SCENE_FACT_QUALITY,
  SCENE_FACT_STATE,
  SCENE_HAS_NO_FILE_YET,
  SCENE_IS_NOT_IN_WHISPARR,
  SEVERAL_IDENTITIES_IN_THIS_NAMESPACE,
  THE_STATUS_READ_DID_NOT_COMPLETE,
  WHISPARR_KEEPS_NO_RECORD_OF_THESE,
  WHISPARR_STATUS_COULD_NOT_BE_READ,
} from "../common/ui/copy";
import { useSceneDetail } from "./useSceneDetail";

/**
 * The sentence each answered refusal reads as, or null where the read never answers it.
 *
 * The vocabulary is shared with the tab's verbs, and a verb's own refusal is stated beside the
 * control that produced it. A read answers none of those, so each maps to null here.
 */
const SENTENCE_FOR_A_REFUSAL: Record<SceneRefusalKind, string | null> = {
  none: null,
  noInstanceConnected: NO_INSTANCE_CONNECTED,
  noIdentityInThisNamespace: NO_IDENTITY_IN_THIS_NAMESPACE,
  severalIdentitiesInThisNamespace: SEVERAL_IDENTITIES_IN_THIS_NAMESPACE,
  capabilityAbsentOnThisGeneration: WHISPARR_KEEPS_NO_RECORD_OF_THESE,
  didNotReachWhisparr: WHISPARR_STATUS_COULD_NOT_BE_READ,
  instanceRefused: INSTANCE_REFUSED,
  instanceOffersNoQualityProfile: null,
  instanceOffersNoRootFolder: null,
  whisparrHasNoEntryForScene: null,
  whisparrAlreadyHoldsThisScene: null,
};

/** How many of the tab's own surfaces one refused answer stops. */
const FACTS_THE_TAB_STATES = 4;

export function WhisparrSceneTab({ entityId }: { entityId: number }) {
  const state = useSceneDetail(entityId);
  const couldNotBeRead = <StatusText kind="error">{WHISPARR_STATUS_COULD_NOT_BE_READ}</StatusText>;

  return (
    <div className="space-y-4">
      <AsyncRegion
        state={deriveAsyncRegionState(state.read)}
        outageNotice={<StatusText kind="warning">{THE_STATUS_READ_DID_NOT_COMPLETE}</StatusText>}
        content={state.view === null ? null : <SceneFacts view={state.view} />}
        // Unreachable, and given the failed node so that reaching it states something. Every
        // successful read carries a view and sets `hasContent`, so the derivation answers `content`
        // for a success and `reading` or `failed` for everything else.
        empty={couldNotBeRead}
        failed={couldNotBeRead}
      />
    </div>
  );
}

function SceneFacts({ view }: { view: SceneDetailView }) {
  const refused = SENTENCE_FOR_A_REFUSAL[view.refusal];
  if (refused !== null) {
    return <RefusalNotice reason={refused} affectedControls={FACTS_THE_TAB_STATES} />;
  }

  return (
    <>
      {view.profileReadDidNotComplete ? (
        <StatusText kind="warning">{THE_STATUS_READ_DID_NOT_COMPLETE}</StatusText>
      ) : null}
      <dl className="space-y-2 rounded-lg border border-border bg-card px-3 py-2">
        <div className="flex items-center gap-3">
          <dt className="text-xs text-secondary">{SCENE_FACT_STATE}</dt>
          <dd className="text-sm text-foreground">
            <StateChip
              state={deriveState({
                excluded: view.excluded,
                present: view.present,
                monitored: view.monitored,
              })}
            />
          </dd>
        </div>
        <FactRow
          label={SCENE_FACT_QUALITY}
          named={view.qualityName}
          absent={SCENE_HAS_NO_FILE_YET}
        />
        <FactRow
          label={SCENE_FACT_PROFILE}
          named={view.qualityProfileName}
          absent={SCENE_IS_NOT_IN_WHISPARR}
        />
        <FactRow
          label={SCENE_FACT_CUTOFF}
          named={view.cutoffName}
          absent={view.present === false ? SCENE_IS_NOT_IN_WHISPARR : SCENE_CUTOFF_NOT_NAMED}
        />
      </dl>
    </>
  );
}

function FactRow({
  label,
  named,
  absent,
}: {
  label: string;
  /** What the instance itself calls this, or null where it names nothing. */
  named: string | null;
  /** What the row states in the value's own place where the instance names nothing. */
  absent: string;
}) {
  return (
    <div className="flex items-center gap-3">
      <dt className="text-xs text-secondary">{label}</dt>
      {/* An instance-supplied name has no bound, so it truncates and carries the whole of itself on
          the element. An absent sentence is this product's own and needs neither. */}
      <dd className="min-w-0 flex-1 truncate text-sm text-foreground" title={named ?? undefined}>
        {named ?? absent}
      </dd>
    </div>
  );
}

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
  SCENE_FACT_STATE,
  SEVERAL_IDENTITIES_IN_THIS_NAMESPACE,
  THE_STATUS_READ_DID_NOT_COMPLETE,
  WHISPARR_KEEPS_NO_RECORD_OF_THESE,
  WHISPARR_STATUS_COULD_NOT_BE_READ,
} from "../common/ui/copy";
import { useSceneDetail } from "./useSceneDetail";

/** The sentence each answered refusal reads as, or null where nothing was refused. */
const SENTENCE_FOR_A_REFUSAL: Record<SceneRefusalKind, string | null> = {
  none: null,
  noInstanceConnected: NO_INSTANCE_CONNECTED,
  noIdentityInThisNamespace: NO_IDENTITY_IN_THIS_NAMESPACE,
  severalIdentitiesInThisNamespace: SEVERAL_IDENTITIES_IN_THIS_NAMESPACE,
  capabilityAbsentOnThisGeneration: WHISPARR_KEEPS_NO_RECORD_OF_THESE,
  didNotReachWhisparr: WHISPARR_STATUS_COULD_NOT_BE_READ,
  instanceRefused: INSTANCE_REFUSED,
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
    <div className="flex items-center gap-3">
      <span className="text-xs text-secondary">{SCENE_FACT_STATE}</span>
      <StateChip
        state={deriveState({
          excluded: view.excluded,
          present: view.present,
          monitored: view.monitored,
        })}
      />
    </div>
  );
}

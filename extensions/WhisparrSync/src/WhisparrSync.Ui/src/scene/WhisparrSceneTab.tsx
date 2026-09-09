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

import type { SceneDetailView } from "../wire/api";
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import { OptionallyDisabled } from "../common/ui/DisabledControl";
import { RefusalNotice } from "../common/ui/RefusalNotice";
import { StateChip } from "../common/ui/StateChip";
import type { WhisparrEntityState } from "../common/ui/stateVocabularyLogic";
import {
  SCENE_CUTOFF_NOT_NAMED,
  SCENE_FACT_CUTOFF,
  SCENE_FACT_PROFILE,
  SCENE_FACT_QUALITY,
  SCENE_FACT_STATE,
  SCENE_HAS_NO_FILE_YET,
  SCENE_IS_NOT_IN_WHISPARR,
  SCENE_UPGRADES_FOLLOW_THE_CUTOFF,
  THE_STATUS_READ_DID_NOT_COMPLETE,
  WHISPARR_STATUS_COULD_NOT_BE_READ,
} from "../common/ui/copy";
import {
  deriveSceneControls,
  sceneControls,
  sceneReadRefusal,
  type SceneControl,
  type SceneVerb,
} from "./sceneControlLogic";
import type { SceneState } from "./sceneStore";
import { useSceneDetail } from "./useSceneDetail";

export function WhisparrSceneTab({ entityId }: { entityId: number }) {
  const { state, act } = useSceneDetail(entityId);
  const couldNotBeRead = <StatusText kind="error">{WHISPARR_STATUS_COULD_NOT_BE_READ}</StatusText>;

  return (
    <div className="space-y-4">
      <AsyncRegion
        state={deriveAsyncRegionState(state.read)}
        outageNotice={<StatusText kind="warning">{THE_STATUS_READ_DID_NOT_COMPLETE}</StatusText>}
        content={
          state.view === null ? null : <SceneSurface scene={state} view={state.view} act={act} />
        }
        // Unreachable, and given the failed node so that reaching it states something. Every
        // successful read carries a view and sets `hasContent`, so the derivation answers `content`
        // for a success and `reading` or `failed` for everything else.
        empty={couldNotBeRead}
        failed={couldNotBeRead}
      />
    </div>
  );
}

function SceneSurface({
  scene,
  view,
  act,
}: {
  scene: SceneState;
  view: SceneDetailView;
  act: (verb: SceneVerb) => void;
}) {
  const refusedRead = sceneReadRefusal(view.refusal);
  if (refusedRead.sentence !== null) {
    return (
      <RefusalNotice
        reason={refusedRead.sentence}
        affectedControls={refusedRead.affectedControls}
      />
    );
  }

  const controls = deriveSceneControls({
    view,
    acting: scene.acting,
    actionFailed: scene.actionFailed,
    actionRefusal: scene.actionRefusal,
    searchIsWithWhisparr: scene.searchIsWithWhisparr,
  });

  return (
    <>
      {view.profileReadDidNotComplete ? (
        <StatusText kind="warning">{THE_STATUS_READ_DID_NOT_COMPLETE}</StatusText>
      ) : null}
      <SceneFacts view={view} state={controls.state} />
      {controls.sharedReason === null ? null : (
        <RefusalNotice
          reason={controls.sharedReason}
          affectedControls={controls.affectedControls}
        />
      )}
      <div className="space-y-2">
        <div className="flex flex-wrap gap-2">
          {sceneControls(controls).map((control) => (
            <SceneControlBlock
              key={control.key}
              control={control}
              onPress={() => {
                act(control.verb);
              }}
            />
          ))}
        </div>
        {controls.statusLine === null ? null : (
          <StatusText kind={controls.statusLine.failed ? "error" : "success"}>
            {controls.statusLine.sentence}
          </StatusText>
        )}
      </div>
    </>
  );
}

function SceneControlBlock({ control, onPress }: { control: SceneControl; onPress: () => void }) {
  return (
    <div className="min-w-0">
      <OptionallyDisabled
        name={control.label}
        onClick={onPress}
        variant={control.variant}
        reason={control.reason}
      />
      {/* Outside the button on purpose: text inside it joins the accessible name, and a control's
          announced name has to be its own name and not a paragraph. */}
      <p className="mt-1 text-xs text-secondary">{control.states}</p>
      {/* Where the reader is deciding whether to spend a search, and the answer to where a separate
          upgrades control went. Stated once per tab, never per control. */}
      {control.key === "search" ? (
        <p className="mt-1 text-xs text-secondary">{SCENE_UPGRADES_FOLLOW_THE_CUTOFF}</p>
      ) : null}
    </div>
  );
}

function SceneFacts({ view, state }: { view: SceneDetailView; state: WhisparrEntityState }) {
  return (
    <dl className="space-y-2 rounded-lg border border-border bg-card px-3 py-2">
      <div className="flex items-center gap-3">
        <dt className="text-xs text-secondary">{SCENE_FACT_STATE}</dt>
        <dd className="text-sm text-foreground">
          <StateChip state={state} />
        </dd>
      </div>
      <FactRow label={SCENE_FACT_QUALITY} named={view.qualityName} absent={SCENE_HAS_NO_FILE_YET} />
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

/**
 * The scene tab, mounted on the video detail page.
 *
 * No outer padding and no max-width wrapper: the host already pads the tab panel. Every visual is
 * a host-emitted utility class, because an extension CSS bundle is page-global and would leak
 * onto every host page.
 */
import { StatusText } from "@cove-extensions/ui-shared";

import type { SceneDetailView } from "../wire/api";
import { AsyncRegion } from "../common/ui/AsyncRegion";
import { deriveAsyncRegionState } from "../common/ui/asyncRegionLogic";
import { OptionallyDisabled } from "../common/ui/DisabledControl";
import { RefusalNotice } from "../common/ui/RefusalNotice";
import { StateChip } from "../common/ui/StateChip";
import { WhisparrLogo } from "../common/ui/WhisparrLogo";
import {
  SCENE_FACT_CUTOFF,
  SCENE_FACT_PROFILE,
  SCENE_FACT_QUALITY,
  SCENE_HEADER_WHISPARR,
  THE_STATUS_READ_DID_NOT_COMPLETE,
  WHISPARR_STATUS_COULD_NOT_BE_READ,
} from "../common/ui/copy";
import {
  deriveSceneControls,
  sceneControls,
  sceneReadRefusal,
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
        // Unreachable: every successful read carries a view and sets `hasContent`. It is given
        // the failed node so that reaching it states something.
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
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          {/* The logo declares the same accessible name as the word beside it, so the pair would
              otherwise announce twice. */}
          <span aria-hidden="true" className="flex">
            <WhisparrLogo className="h-4 w-4" />
          </span>
          <span className="text-xs font-semibold uppercase tracking-wide text-secondary">
            {SCENE_HEADER_WHISPARR}
          </span>
        </div>
        <StateChip state={controls.state} />
      </div>
      {view.profileReadDidNotComplete ? (
        <StatusText kind="warning">{THE_STATUS_READ_DID_NOT_COMPLETE}</StatusText>
      ) : null}
      <SceneFacts view={view} />
      {controls.sharedReason === null ? null : (
        <RefusalNotice
          reason={controls.sharedReason}
          affectedControls={controls.affectedControls}
        />
      )}
      <div className="space-y-2">
        <div className="rounded-lg border border-border">
          {sceneControls(controls).map((control, index) => (
            <div
              key={control.key}
              className={index === 0 ? "px-3 py-2" : "border-t border-border px-3 py-2"}
            >
              <OptionallyDisabled
                name={control.label}
                onClick={() => {
                  act(control.verb);
                }}
                variant={control.variant}
                fill
                reason={control.reason}
              />
            </div>
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

function SceneFacts({ view }: { view: SceneDetailView }) {
  const facts = (
    [
      [SCENE_FACT_QUALITY, view.qualityName],
      [SCENE_FACT_PROFILE, view.qualityProfileName],
      [SCENE_FACT_CUTOFF, view.cutoffName],
    ] as const
  ).flatMap(([label, named]) => (named === null ? [] : [{ label, named }]));

  if (facts.length === 0) return null;

  return (
    <dl className="space-y-2 rounded-lg border border-border bg-card px-3 py-2">
      {facts.map((fact) => (
        <FactRow key={fact.label} label={fact.label} named={fact.named} />
      ))}
    </dl>
  );
}

function FactRow({ label, named }: { label: string; named: string }) {
  return (
    <div className="flex items-center gap-3">
      <dt className="text-xs text-secondary">{label}</dt>
      {/* An instance-supplied name has no length bound, so it truncates and carries the whole of
          itself in the title. */}
      <dd className="min-w-0 flex-1 truncate text-right text-sm text-foreground" title={named}>
        {named}
      </dd>
    </div>
  );
}

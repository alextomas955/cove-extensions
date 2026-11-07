import {
  Button,
  ProgressBar,
  SectionCard,
  Spinner,
  StatusPill,
  StatusText,
} from "@cove-extensions/ui-shared";

import type { RunStageView } from "./run";
import {
  RUN_STAGES,
  RUN_STAGE_LABELS,
  RUN_STAGE_STATE_LABELS,
  RUN_STAGE_STATE_VARIANTS,
  isRunInFlight,
  runEndText,
  stageInFlightOf,
  stageOutcomeText,
  stageProgressPercent,
  stageTallyText,
} from "./runStageLogic";
import { useRun } from "./useRun";

const WAITING: readonly RunStageView[] = RUN_STAGES.map((stage) => ({
  stage,
  state: "pending",
  jobId: null,
  detail: null,
}));

/** What the pass in flight is doing: how far it has got, what it is on, and what it has counted. */
function StageProgress({ stage }: { stage: RunStageView }) {
  const tally = stageTallyText(stage.detail);
  return (
    <>
      <ProgressBar
        percent={stageProgressPercent(stage.detail)}
        label={`${RUN_STAGE_LABELS[stage.stage]} progress`}
      />
      {stage.detail?.subTask == null ? null : (
        <p className="truncate text-xs text-muted">{stage.detail.subTask}</p>
      )}
      {tally === null ? null : <StatusText kind="muted">{tally}</StatusText>}
    </>
  );
}

/** What one pass did, once the run has ended. */
function StageOutcome({ stage }: { stage: RunStageView }) {
  const outcome = stageOutcomeText(stage);
  if (outcome === null) return null;
  return <StatusText kind={stage.state === "failed" ? "error" : "muted"}>{outcome}</StatusText>;
}

/** The run: one button, and where each of the four stages has got to. */
export function RunSection() {
  const { run, loading, forbidden, failure, claiming, cancelling, start, cancel } = useRun();

  if (forbidden) {
    return (
      <SectionCard title="Run">
        <StatusText kind="warning">
          This account cannot start or watch a run. It needs permission to scan the library, run
          jobs, identify videos and configure extensions.
        </StatusText>
      </SectionCard>
    );
  }

  const stages = run === null ? WAITING : run.stages;
  const inFlight = isRunInFlight(run ?? undefined);
  const moving = run === null ? null : stageInFlightOf(run);
  const endText = run === null ? null : runEndText(run);

  return (
    <SectionCard
      title="Run"
      description="Scan, fill in the missing fingerprints, identify, then generate the images. Each stage starts only once the one before it has finished."
    >
      {loading ? (
        <div className="flex items-center gap-2 text-sm text-secondary">
          <Spinner />
          Reading the current run…
        </div>
      ) : null}

      <div className="flex items-center gap-2">
        <Button onClick={start} disabled={inFlight || claiming}>
          {claiming ? "Starting…" : "Run"}
        </Button>
        {inFlight ? (
          <Button variant="ghost" onClick={cancel} disabled={cancelling}>
            {cancelling ? "Cancelling…" : "Cancel"}
          </Button>
        ) : null}
        {endText === null ? null : (
          <StatusText kind={run?.endedReason === "completed" ? "success" : "muted"}>
            {endText}
          </StatusText>
        )}
      </div>

      <ul className="space-y-2">
        {stages.map((stage) => (
          <li key={stage.stage} data-stage={stage.stage} className="space-y-1 text-sm">
            <div className="flex items-center justify-between gap-2">
              <span className="text-secondary">{RUN_STAGE_LABELS[stage.stage]}</span>
              <span data-state={stage.state}>
                <StatusPill variant={RUN_STAGE_STATE_VARIANTS[stage.state]}>
                  {RUN_STAGE_STATE_LABELS[stage.state]}
                </StatusPill>
              </span>
            </div>
            {stage.stage === moving?.stage ? <StageProgress stage={stage} /> : null}
            {run?.endedAt == null ? null : <StageOutcome stage={stage} />}
          </li>
        ))}
      </ul>

      {failure === null ? null : <StatusText kind="error">{failure}</StatusText>}
    </SectionCard>
  );
}

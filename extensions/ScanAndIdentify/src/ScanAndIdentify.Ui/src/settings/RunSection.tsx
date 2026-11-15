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
function StageProgress({ stage }: Readonly<{ stage: RunStageView }>) {
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
function StageOutcome({ stage }: Readonly<{ stage: RunStageView }>) {
  const outcome = stageOutcomeText(stage);
  if (outcome === null) return null;
  return <StatusText kind={stage.state === "failed" ? "error" : "muted"}>{outcome}</StatusText>;
}

/**
 * One pass, as a tile in the row the four of them form.
 *
 * The row reads left to right in the order the passes run, so the position carries the sequence and
 * nothing numbers them. Only the pass in flight is drawn with the accent border; the others keep
 * the plain hairline, so which one is moving is legible without reading a single word.
 */
function StageTile({
  stage,
  moving,
  ended,
}: Readonly<{ stage: RunStageView; moving: boolean; ended: boolean }>) {
  return (
    <li
      data-stage={stage.stage}
      className={`min-w-0 flex-1 rounded-lg border p-3 ${
        moving ? "border-accent bg-card" : "border-border bg-card/40"
      }`}
    >
      {/* Stacked rather than set opposite each other: a tile is a quarter of a settings panel
          wide, so a name on the left and its state on the right read as two separate things. */}
      <div className="flex flex-col items-start gap-1.5">
        <span className="min-w-0 text-sm font-medium text-foreground">
          {RUN_STAGE_LABELS[stage.stage]}
        </span>
        <span data-state={stage.state}>
          <StatusPill variant={RUN_STAGE_STATE_VARIANTS[stage.state]}>
            {RUN_STAGE_STATE_LABELS[stage.state]}
          </StatusPill>
        </span>
      </div>
      {moving ? (
        <div className="mt-2 space-y-1">
          <StageProgress stage={stage} />
        </div>
      ) : null}
      {ended ? (
        <div className="mt-2">
          <StageOutcome stage={stage} />
        </div>
      ) : null}
    </li>
  );
}

/** Why a run cannot start right now, or null when it can. */
const DIRTY_OPTIONS =
  "Save the run options first. A run reads what is saved, not what is on screen.";

/** The run: one button, and where each of the four stages has got to. */
export function RunSection({ optionsDirty }: Readonly<{ optionsDirty: boolean }>) {
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
      description="Finds library items Cove holds no metadata for and identifies them. Each pass starts only once the one before it has finished."
      headerRight={
        <div className="flex items-center gap-2">
          {inFlight ? (
            <Button variant="ghost" onClick={cancel} disabled={cancelling}>
              {cancelling ? "Cancelling…" : "Cancel"}
            </Button>
          ) : null}
          <Button
            onClick={start}
            disabled={inFlight || claiming || optionsDirty}
            describedBy={optionsDirty ? "scanandidentify-run-blocked" : undefined}
          >
            {claiming ? "Starting…" : "Run"}
          </Button>
        </div>
      }
    >
      {/* The one constraint that decides whether a run finishes, so it is drawn as a notice rather
          than set in the body text it used to sit in. */}
      <p
        className={`max-w-3xl border-l-2 py-1 pl-3 text-sm ${
          inFlight ? "border-accent text-foreground" : "border-border text-secondary"
        }`}
      >
        Keep this page open. A run advances only while it is open, and stops where it is if you
        close it. It covers your whole library, which on a large one takes hours.
      </p>

      {loading ? (
        <div className="flex items-center gap-2 text-sm text-secondary">
          <Spinner />
          Reading the current run…
        </div>
      ) : null}

      {/* Four tiles in the order the passes run, wrapping to a column on a narrow panel. */}
      <ul className="flex flex-col gap-2 sm:flex-row sm:items-stretch">
        {stages.map((stage) => (
          <StageTile
            key={stage.stage}
            stage={stage}
            moving={stage.stage === moving?.stage}
            ended={run?.endedAt != null}
          />
        ))}
      </ul>

      {optionsDirty ? (
        <output id="scanandidentify-run-blocked" className="block">
          <StatusText kind="warning">{DIRTY_OPTIONS}</StatusText>
        </output>
      ) : null}
      {endText === null ? null : (
        <StatusText kind={run?.endedReason === "completed" ? "success" : "muted"}>
          {endText}
        </StatusText>
      )}
      {failure === null ? null : <StatusText kind="error">{failure}</StatusText>}
    </SectionCard>
  );
}

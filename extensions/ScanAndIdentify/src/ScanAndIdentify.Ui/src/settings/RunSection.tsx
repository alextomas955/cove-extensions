import { Button, SectionCard, Spinner, StatusPill, StatusText } from "@cove-extensions/ui-shared";

import type { RunEndReason, RunStageView } from "./run";
import {
  RUN_STAGES,
  RUN_STAGE_LABELS,
  RUN_STAGE_STATE_LABELS,
  RUN_STAGE_STATE_VARIANTS,
  isRunInFlight,
} from "./runStageLogic";
import { useRun } from "./useRun";

// `cancelled` is the record's reason both for a person stopping a run and for one nothing could take
// further, so it reads as stopped rather than as cancelled by you.
const END_TEXT: Record<RunEndReason, string> = {
  completed: "The run finished.",
  failed: "The run stopped: a stage failed.",
  cancelled: "The run stopped.",
  interrupted: "The run was interrupted: Cove no longer knows about one of its jobs.",
};

const WAITING: readonly RunStageView[] = RUN_STAGES.map((stage) => ({
  stage,
  state: "pending",
  jobId: null,
}));

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
        {run?.endedReason == null ? null : (
          <StatusText kind={run.endedReason === "completed" ? "success" : "muted"}>
            {END_TEXT[run.endedReason]}
          </StatusText>
        )}
      </div>

      <ul className="space-y-2">
        {stages.map((stage) => (
          <li
            key={stage.stage}
            data-stage={stage.stage}
            className="flex items-center justify-between gap-2 text-sm"
          >
            <span className="text-secondary">{RUN_STAGE_LABELS[stage.stage]}</span>
            <span data-state={stage.state}>
              <StatusPill variant={RUN_STAGE_STATE_VARIANTS[stage.state]}>
                {RUN_STAGE_STATE_LABELS[stage.state]}
              </StatusPill>
            </span>
          </li>
        ))}
      </ul>

      {failure === null ? null : <StatusText kind="error">{failure}</StatusText>}
    </SectionCard>
  );
}

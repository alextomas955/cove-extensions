/**
 * The stand-in server and the whole-record helper the run suites share.
 *
 * The request double refuses a call no test configured, so a suite that forgets to seed a response
 * fails loudly instead of driving the chain against something nobody chose.
 */
import type { RunEndReason, RunStage, RunStageState, RunView } from "./run";
import { answered } from "./testRunOptions";

/** The stage order, written out rather than read from the module the suites are testing. */
const STAGES: readonly RunStage[] = ["scan", "fingerprints", "identify", "images"];

/** One request the page issued through the stand-in. */
export interface RecordedCall {
  path: string;
  method: string;
  body: unknown;
}

/** A request failure as the real module raises one: a status and a body. */
export class FakeApiError extends Error {
  constructor(
    readonly status: number,
    readonly body: string,
  ) {
    super(`${status} ${body}`);
  }
}

export const server = {
  calls: [] as RecordedCall[],
  /** What each successive run read answers; the last entry repeats. Undefined is the 204. */
  reads: [] as (RunView | undefined)[],
  readError: null as Error | null,
  claimAnswer: null as RunView | null,
  claimError: null as Error | null,
  stageError: null as Error | null,
  hostError: null as Error | null,
  hostJobId: "scan-job",
  /** What the host's job cancel fails with. Null means it answers 200. */
  cancelError: null as Error | null,
};

export function resetServer(): void {
  server.calls = [];
  server.reads = [];
  server.readError = null;
  server.claimAnswer = null;
  server.claimError = null;
  server.stageError = null;
  server.hostError = null;
  server.hostJobId = "scan-job";
  server.cancelError = null;
}

/** The run the read answers next, leaving the last seeded one in place once the queue is spent. */
function nextRead(): RunView | undefined {
  if (server.reads.length === 0) throw new Error("unconfigured run read");
  return server.reads.length === 1 ? server.reads[0] : server.reads.shift();
}

/** The module shape `vi.mock` returns for `@cove-extensions/ui-shared/extensionRequest`. */
export function extensionRequestDouble() {
  const record = (path: string, options: RequestInit | undefined): unknown => {
    const body: unknown = typeof options?.body === "string" ? JSON.parse(options.body) : undefined;
    server.calls.push({ path, method: options?.method ?? "GET", body });
    return body;
  };

  return {
    ApiError: FakeApiError,
    errorText: (err: unknown) => (err instanceof Error ? err.message : String(err)),
    request: (path: string, options?: RequestInit) => {
      record(path, options);
      if (path.startsWith("/jobs/")) {
        return server.cancelError === null
          ? Promise.resolve(undefined)
          : Promise.reject(server.cancelError);
      }
      if (server.readError !== null) return Promise.reject(server.readError);
      return Promise.resolve(nextRead());
    },
    requestJson: (path: string, options?: RequestInit) => {
      const sent = record(path, options);
      if (path.endsWith("/run")) {
        if (server.claimError !== null) return Promise.reject(server.claimError);
        if (server.claimAnswer === null) throw new Error(`unconfigured claim to ${path}`);
        return Promise.resolve(server.claimAnswer);
      }
      if (path.endsWith("/run/stages")) {
        if (server.stageError !== null) return Promise.reject(server.stageError);
        return Promise.resolve(nextRead());
      }
      if (path.endsWith("/run/end")) {
        const { reason } = sent as { reason: RunEndReason };
        const current = nextRead();
        if (current === undefined) throw new Error("ended a run the read does not know about");
        return Promise.resolve({
          ...current,
          endedAt: "2026-10-30T10:00:00Z",
          endedReason: reason,
        });
      }
      if (server.hostError !== null) return Promise.reject(server.hostError);
      return Promise.resolve({ jobId: server.hostJobId });
    },
  };
}

/**
 * A whole run, the way the route answers one. The field names and the claim-time stage states are
 * copied from the C# contract, so a field the server stops writing is a type error here rather than
 * an invented value.
 */
export const runView = (
  states: Partial<Record<RunStage, RunStageState>> = {},
  overrides: Partial<RunView> = {},
  jobIds: Partial<Record<RunStage, string>> = {},
): RunView => ({
  runId: "run-1",
  startedAt: "2026-10-30T09:00:00Z",
  endedAt: null,
  endedReason: null,
  options: answered(),
  stages: STAGES.map((stage) => ({
    stage,
    state: states[stage] ?? "pending",
    jobId: jobIds[stage] ?? null,
  })),
  ...overrides,
});

/** The paths of every call the page made, in order. */
export const calledPaths = (): string[] => server.calls.map((call) => call.path);

/**
 * The stand-in Cove and the whole-record helper the run suites share.
 *
 * The seam is the host's own fetch, which the generated client reaches through the adapter in
 * common/lib/wireClient and the host-route helper calls directly, so what is scripted here is the
 * request the page really issues. A call no test configured is refused, so a suite that forgets to
 * seed a response fails loudly instead of driving the chain against something nobody chose.
 */
import type { RunEndReason, RunStage, RunStageDetail, RunStageState, RunView } from "./run";
import { answered } from "./testRunOptions";

/** The stage order, written out rather than read from the module the suites are testing. */
const STAGES: readonly RunStage[] = ["scan", "fingerprints", "identify", "images"];

const RUN_PATH = "/extensions/com.alextomas955.scanandidentify/run";

/** One request the page issued through the stand-in. */
export interface RecordedCall {
  path: string;
  method: string;
  body: unknown;
}

/** A reply the server refuses with: the status it sends and the body it writes. */
export interface Refusal {
  status: number;
  body: string;
}

/** A refusal the stand-in answers with, in the two parts an error branch reads. */
export const refusal = (status: number, body: string): Refusal => ({ status, body });

export const server = {
  calls: [] as RecordedCall[],
  /** What each successive run read answers; the last entry repeats. Undefined is the 204. */
  reads: [] as (RunView | undefined)[],
  readError: null as Refusal | null,
  /** Holds the next run read open, so a test can act while one is in flight. */
  holdNextRead: false,
  releaseRead: (): void => undefined,
  claimAnswer: null as RunView | null,
  claimError: null as Refusal | null,
  /** What the write that claims a stage fails with. The one carrying a job id is `reportError`. */
  stageError: null as Refusal | null,
  /** What the write that reports a started job's id fails with. */
  reportError: null as Refusal | null,
  /** Holds the next host start open, which is the window a cancel cannot see a job id in. */
  holdHostStart: false,
  releaseHostStart: (): void => undefined,
  hostError: null as Refusal | null,
  hostJobId: "scan-job",
  /** What the host's job cancel fails with. Null means it answers 200. */
  cancelError: null as Refusal | null,
};

export function resetServer(): void {
  server.calls = [];
  server.reads = [];
  server.readError = null;
  server.claimAnswer = null;
  server.claimError = null;
  server.stageError = null;
  server.reportError = null;
  server.holdNextRead = false;
  server.releaseRead = () => undefined;
  server.holdHostStart = false;
  server.releaseHostStart = () => undefined;
  server.hostError = null;
  server.hostJobId = "scan-job";
  server.cancelError = null;
}

/** The run the read answers next, leaving the last seeded one in place once the queue is spent. */
function nextRead(): RunView | undefined {
  if (server.reads.length === 0) throw new Error("unconfigured run read");
  return server.reads.length === 1 ? server.reads[0] : server.reads.shift();
}

// The generated client reads a reply by its Content-Type, so every reply carries one: a bare
// Response takes a different path through the client than the server's own reply does.
// A bodyless reply carries no Content-Type: the generated client reads a reply by that header, and
// a 204 that claims JSON is parsed as an empty document and fails with "Unexpected end of JSON
// input" before any handler sees the status.
const reply = (value: unknown, status = 200): Response =>
  value === undefined
    ? new Response(null, { status: status === 200 ? 204 : status })
    : new Response(JSON.stringify(value), {
        status,
        headers: { "Content-Type": "application/json" },
      });

// Waits only when a test asked to hold this call open, so every other request stays synchronous.
const held = (hold: boolean, keep: (release: () => void) => void): Promise<void> =>
  hold
    ? new Promise<void>((resolve) => {
        keep(resolve);
      })
    : Promise.resolve();

const refused = ({ status, body }: Refusal): Response =>
  new Response(body, { status, headers: { "Content-Type": "application/json" } });

/** The module shape `vi.mock` returns for `@cove/runtime/api`. */
export function coveRuntimeApiDouble() {
  return {
    extensionFetch: (path: string, init: RequestInit = {}): Promise<Response> => {
      // Recorded without the `/api` prefix every caller shares, so a recorded path reads as the
      // route rather than as the mount point.
      const route = path.startsWith("/api") ? path.slice(4) : path;
      const method = (init.method ?? "GET").toUpperCase();
      // Parsed only when there is something to parse: a POST with no body reaches here with an
      // empty string, which JSON.parse refuses.
      const body: unknown =
        typeof init.body === "string" && init.body !== "" ? JSON.parse(init.body) : undefined;
      server.calls.push({ path: route, method, body });

      if (route.startsWith("/jobs/")) {
        return Promise.resolve(
          server.cancelError === null ? reply(undefined) : refused(server.cancelError),
        );
      }

      if (route.startsWith("/metadata/")) {
        // Consumed as the call begins, not as it resolves, so a test can wait for the hold to be
        // taken rather than for the answer it is holding back.
        const hold = server.holdHostStart;
        server.holdHostStart = false;
        return held(hold, (release) => (server.releaseHostStart = release)).then(() =>
          server.hostError === null
            ? reply({ jobId: server.hostJobId })
            : refused(server.hostError),
        );
      }

      if (route === `${RUN_PATH}/stages`) {
        const failure =
          (body as { jobId: string | null }).jobId === null
            ? server.stageError
            : server.reportError;
        return Promise.resolve(failure === null ? reply(nextRead()) : refused(failure));
      }

      if (route === `${RUN_PATH}/end`) {
        const { reason } = body as { reason: RunEndReason };
        const current = nextRead();
        if (current === undefined) throw new Error("ended a run the read does not know about");
        return Promise.resolve(
          reply({ ...current, endedAt: "2026-10-30T10:00:00Z", endedReason: reason }),
        );
      }

      if (route === RUN_PATH && method === "POST") {
        if (server.claimError !== null) return Promise.resolve(refused(server.claimError));
        if (server.claimAnswer === null) throw new Error(`unconfigured claim to ${route}`);
        return Promise.resolve(reply(server.claimAnswer));
      }

      if (route === RUN_PATH) {
        const hold = server.holdNextRead;
        server.holdNextRead = false;
        return held(hold, (release) => (server.releaseRead = release)).then(() =>
          server.readError === null ? reply(nextRead()) : refused(server.readError),
        );
      }

      throw new Error(`unconfigured ${method} to ${route}`);
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
  details: Partial<Record<RunStage, RunStageDetail>> = {},
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
    detail: details[stage] ?? null,
  })),
  ...overrides,
});

/** One stage detail, the way the route answers one: every member present, most of them null. */
export const stageDetail = (overrides: Partial<RunStageDetail> = {}): RunStageDetail => ({
  progress: null,
  subTask: null,
  unitsTotal: null,
  unitsCompleted: null,
  unitsSucceeded: null,
  unitsFailed: null,
  unitsSkipped: null,
  summary: null,
  error: null,
  ...overrides,
});

/** The paths of every call the page made, in order. */
export const calledPaths = (): string[] => server.calls.map((call) => call.path);

// Scan and Identify's wiring on top of the shared harness at tests/e2e/: this extension's own build
// paths, the route constants its specs address, and the readers they share.
//
// Every spec here takes an instance of its own through `isolatedHarnessFixtures`, because the run
// record is one global key and a worker-shared instance would show each spec its neighbour's run.
// So this file declares no worker-scoped `extension` option; the extension goes to those fixtures
// directly.
//
// The harness is imported by package name through npm workspaces. A second @playwright/test install
// under this directory would break Playwright's module singleton, so this must never declare one.
import { expect, createApiClient } from "@cove-extensions/e2e";
import { resolveExtensionPaths } from "@cove-extensions/e2e/resolve-extension";

export const EXTENSION_ID = "com.alextomas955.scanandidentify";
export const ROUTE = `/api/extensions/${EXTENSION_ID}`;
export const RUN_ROUTE = `${ROUTE}/run`;

export const SCAN_AND_IDENTIFY_EXTENSION = resolveExtensionPaths(import.meta.url, {
  srcProject: "ScanAndIdentify",
});

/** The stages in the order a run executes them, copied from the C# RunStage enum. */
export const STAGES = ["scan", "fingerprints", "identify", "images"];

export { expect };

/**
 * An API client for a harness that outlives a restart: both the address and the token are read on
 * every call, because a restart re-mints the token and may republish the port.
 */
export function clientFor(harness) {
  return createApiClient(
    () => harness.baseUrl,
    () => harness.token,
  );
}

/** The run in flight, or undefined when the route answers its 204. */
export async function readRun(api) {
  const answer = await api.get(RUN_ROUTE);
  expect(
    [200, 204].includes(answer.status),
    `reading the run answered ${answer.status}: ${answer.text}`,
  ).toBe(true);
  return answer.status === 204 ? undefined : answer.json;
}

/** One stage of a run view, by name. */
export const stageOf = (run, stage) => run.stages.find((entry) => entry.stage === stage);

export { seedVideo } from "@cove-extensions/e2e/seed-media";
export { attemptUntil } from "@cove-extensions/e2e/poll";

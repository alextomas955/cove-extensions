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
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, createApiClient } from "@cove-extensions/e2e";
import { resolveExtensionPaths } from "@cove-extensions/e2e/resolve-extension";

// The harness's own media fixtures, found through the package rather than by counting `..`
// segments from this file, so moving either directory is a resolution error and not a copy of
// nothing.
const FIXTURES_DIR = join(
  dirname(fileURLToPath(import.meta.resolve("@cove-extensions/e2e/seed-media"))),
  "fixtures-media",
);

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

/**
 * Copies a media fixture into the container's library path and registers it with nothing, which is
 * what gives a scan something to discover. `seedVideo` imports through the host's own route, so a
 * library seeded with it is already complete and a scan over it is a scan that finds nothing.
 *
 * @returns the path the file now has inside the container.
 */
export async function placeUnregisteredVideo(harness, name) {
  const containerPath = `/data/${name}`;
  await harness.container.copyFilesToContainer([
    { source: join(FIXTURES_DIR, "test-video.mp4"), target: containerPath },
  ]);
  // Copied in as root, and the scan reads as the host's own user.
  await harness.container.exec(["chown", "cove:cove", containerPath], { user: "root" });
  return containerPath;
}

/** The video whose file sits at `path`, or undefined while the library holds none. */
export async function videoAtPath(api, path) {
  const answer = await api.get("/api/videos?page=1&perPage=200");
  expect(answer.ok, `listing videos answered ${answer.status}: ${answer.text}`).toBe(true);
  const items = answer.json.items ?? answer.json;
  return items.find((video) => video.files?.some((file) => file.path === path));
}

/** The fingerprint kinds recorded against a video's files, as the detail route reports them. */
export async function fingerprintKinds(api, videoId) {
  const answer = await api.get(`/api/videos/${videoId}`);
  expect(answer.ok, `reading video ${videoId} answered ${answer.status}: ${answer.text}`).toBe(
    true,
  );
  return answer.json.files.flatMap((file) => file.fingerprints.map((print) => print.type));
}

export { attemptUntil } from "@cove-extensions/e2e/poll";

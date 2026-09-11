// Whisparr Sync's wiring on top of the shared harness at tests/e2e/: pre-fills the `extension`
// fixture option with this extension's own build paths, and re-exports the shared helpers.
//
// The harness is imported BY PACKAGE NAME through npm workspaces. A second @playwright/test install
// under this directory would break Playwright's module singleton, so this must never declare one.
//
// This file must stay at e2e/lib/: resolveExtensionPaths walks a fixed number of parents from the
// caller's own module URL, so moving it silently relocates every path it derives.
import { test as baseTest } from "@cove-extensions/e2e";
import { resolveExtensionPaths } from "@cove-extensions/e2e/resolve-extension";
import { whisparrFixtures } from "@cove-extensions/e2e/whisparr";

export const WHISPARR_SYNC_EXTENSION = resolveExtensionPaths(import.meta.url, {
  srcProject: "WhisparrSync",
});

// The Whisparr fixtures are taken here rather than offered by the shared harness. Nothing outside
// this extension starts a Whisparr, and a fixture on the shared `test` would be loaded by every
// extension's specs whether or not they have one.
export const test = baseTest.extend({
  extension: [WHISPARR_SYNC_EXTENSION, { option: true }],
  ...whisparrFixtures(),
});

export { expect } from "@cove-extensions/e2e";

// Used below as well as re-exported, and `export ... from` binds nothing locally.
import { extensionRoute } from "./contract.mjs";

// Re-exported so a spec has one import site for everything the fixtures module offers. The values
// themselves live in contract.mjs, written once.
export {
  EXTENSION_ID,
  extensionRoute,
  SETTLE_DWELL_MS,
  STASHDB_ENDPOINT,
  THEPORNDB_ENDPOINT,
  WHISPARR_ROOT,
} from "./contract.mjs";

/**
 * Points this extension at one started Whisparr instance.
 *
 * The address is the instance's IN-NETWORK one: the request under test leaves Cove's own process,
 * and a Cove container has no route to the host-published port a test process uses.
 */
export async function connectWhisparr(api, whisparr, generation) {
  const credential = {
    address: whisparr[generation].internalBaseUrl,
    keyWrite: "replace",
    apiKey: whisparr.apiKey,
  };
  const saved = await api.put(extensionRoute("settings"), {
    selectedGeneration: generation,
    v3: generation === "v3" ? credential : null,
    v2: generation === "v2" ? credential : null,
  });
  if (saved.status !== 200) {
    throw new Error(
      `connectWhisparr: PUT ${extensionRoute("settings")} answered ${saved.status}: ${saved.text?.slice(0, 300)}`,
    );
  }
  return saved.json;
}

/**
 * Creates one Cove studio, optionally carrying the identity rows the connected generation reads.
 *
 * A studio created with no `remoteIds` is the unreachable case D-08 calls ordinary rather than rare:
 * roughly a tenth of the owner's studios carry no id in the v3 namespace.
 */
export async function seedCoveStudio(api, { name, remoteIds = [] }) {
  return created(await api.post("/api/studios", { name, remoteIds }), "POST /api/studios");
}

/** @see seedCoveStudio */
export async function seedCovePerformer(api, { name, remoteIds = [] }) {
  return created(await api.post("/api/performers", { name, remoteIds }), "POST /api/performers");
}

/**
 * Creates one Cove video, optionally carrying the identity rows the connected generation reads.
 *
 * No file: a video carries its identity rows whether or not the library holds a file for it, and the
 * card a status is drawn on is the one the grid draws from the video.
 *
 * @see seedCoveStudio
 */
export async function seedCoveVideo(api, { title, remoteIds = [], studioId }) {
  return created(
    await api.post("/api/videos", {
      title,
      remoteIds,
      // Omitted rather than sent as null, so a caller naming no studio leaves the field to the host
      // rather than writing an absence over whatever it defaults to.
      ...(studioId === undefined ? {} : { studioId }),
    }),
    "POST /api/videos",
  );
}

function created(response, what) {
  if (response.status >= 300) {
    throw new Error(`${what} answered ${response.status}: ${response.text?.slice(0, 300)}`);
  }
  if (typeof response.json?.id !== "number") {
    throw new Error(
      `${what} answered ${response.status} with no id, so nothing below could address the entity it made: ${response.text?.slice(0, 300)}`,
    );
  }
  return response.json;
}

/**
 * The command roster and the queue total, which is the observable form of a started search.
 *
 * Both are read from the instance itself. The roster is never expected to be EMPTY: the instance
 * runs scheduled tasks of its own and they appear here. What a spec asserts on is the absence of a
 * command whose name says it searches.
 */
export async function whisparrActivity(api) {
  const commands = await api.get("/api/v3/command");
  const queue = await api.get("/api/v3/queue");
  return {
    commandNames: (Array.isArray(commands.json) ? commands.json : []).map(
      (command) => command.name,
    ),
    queueTotal: queue.json?.totalRecords ?? null,
  };
}

/**
 * What the instance could acquire with, which is what bounds every never-searched claim taken
 * against it.
 *
 * Read rather than assumed: a fixture that grew an indexer would make these gestures acquisitive and
 * no assertion in either spec would notice.
 */
export async function whisparrAcquisitionSurface(api) {
  const indexers = await api.get("/api/v3/indexer");
  const downloadClients = await api.get("/api/v3/downloadclient");
  return {
    indexers: Array.isArray(indexers.json) ? indexers.json.length : null,
    downloadClients: Array.isArray(downloadClients.json) ? downloadClients.json.length : null,
  };
}

/** One entity as the instance holds it right now, or null where it holds none. */
export async function whisparrEntity(api, kind, foreignId) {
  const read = await api.get(`/api/v3/${kind}/${encodeURIComponent(foreignId)}`);
  return read.status === 200 ? read.json : null;
}

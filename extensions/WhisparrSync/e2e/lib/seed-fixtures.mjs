// Seeds the synthetic fixture corpus (studios / performers / scenes + thumbnails) into a running Cove
// instance through Cove's own API, so the WhisparrSync surfaces run against a known, publish-safe dataset.
//
// SECRET / CONTENT SAFETY: every path resolves from this suite's own `fixtures/` directory only — the
// committed synthetic corpus. NO real media, no StashDB, no dev credentials are ever
// referenced here. Each scene is registered from the one shared tiny fixture video (copied to the scene's
// synthetic path inside the container), then its Cove-owned title / studio / performers are attached
// through Cove's real metadata endpoints.
//
// Cove has no bulk import, so studios and performers are created first and then linked to each video. The
// scene registration (POST /api/videos/from-file) is the load-bearing step every consumer depends on; the
// metadata enrichment is best-effort so a Cove build that renames a metadata endpoint degrades to
// "videos seeded, links skipped" rather than failing the whole suite.
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { readFileSync } from 'node:fs';

const HERE = dirname(fileURLToPath(import.meta.url)); // …/extensions/WhisparrSync/e2e/lib
const REPO_ROOT = join(HERE, '..', '..', '..', '..');
const FIXTURES_DIR = join(HERE, '..', 'fixtures'); // …/extensions/WhisparrSync/e2e/fixtures
const SHARED_VIDEO = join(REPO_ROOT, 'tests', 'e2e', 'lib', 'fixtures-media', 'test-video.mp4');

function readMetadata(name) {
  return JSON.parse(readFileSync(join(FIXTURES_DIR, 'metadata', name), 'utf8'));
}

/** POST helper returning the parsed body (or null on a non-2xx / unparseable response — enrichment is best-effort). */
async function postJson(baseUrl, path, body) {
  try {
    const res = await fetch(`${baseUrl}${path}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!res.ok) return null;
    const text = await res.text();
    return text ? JSON.parse(text) : {};
  } catch {
    return null;
  }
}

/** PUT helper (best-effort) used to link a created video to its studio / performers / title. */
async function putJson(baseUrl, path, body) {
  try {
    const res = await fetch(`${baseUrl}${path}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    return res.ok;
  } catch {
    return false;
  }
}

// Create each fixture studio / performer once and return a synthetic-id → Cove-id map. A Cove instance
// that names these endpoints differently just yields an empty map, and the videos still seed unlinked.
async function seedEntities(baseUrl, records, apiPath) {
  const map = new Map();
  for (const record of records) {
    const created = await postJson(baseUrl, apiPath, { name: record.name });
    if (created && typeof created.id !== 'undefined') {
      map.set(record.id, created.id);
    }
  }
  return map;
}

/**
 * Seeds the whole synthetic corpus into a running Cove instance.
 *
 * @param {{ container: import('testcontainers').StartedTestContainer, baseUrl: string }} args
 *   - `container` is the harness's Cove container (for copying the fixture video in), `baseUrl` its API root.
 * @returns {Promise<Map<string, { coveVideoId: number, whisparrState: string, studioId: number|null, performerIds: number[] }>>}
 *   keyed by synthetic scene id.
 */
export async function seedCorpus({ container, baseUrl }) {
  const scenes = readMetadata('scenes.json');
  const studios = readMetadata('studios.json');
  const performers = readMetadata('performers.json');

  const studioIds = await seedEntities(baseUrl, studios, '/api/studios');
  const performerIds = await seedEntities(baseUrl, performers, '/api/performers');

  const result = new Map();
  for (const scene of scenes) {
    // Register the scene as a Cove video from the shared tiny fixture, placed at the scene's synthetic path.
    const containerPath = `/data${scene.path}`; // e.g. /data/synthetic/scene-alpha/scene-alpha.mp4
    await container.exec(['mkdir', '-p', dirname(containerPath)], { user: 'root' });
    await container.copyFilesToContainer([{ source: SHARED_VIDEO, target: containerPath }]);
    await container.exec(['chown', 'cove:cove', containerPath], { user: 'root' });

    const created = await postJson(baseUrl, '/api/videos/from-file', { filePath: containerPath });
    if (!created || typeof created.id === 'undefined') {
      throw new Error(`seedCorpus: could not register scene ${scene.id} as a Cove video`);
    }

    // Best-effort metadata enrichment: title + studio + performers via Cove's video update endpoint.
    const linkedStudio = studioIds.get(scene.studio) ?? null;
    const linkedPerformers = scene.performers
      .map((p) => performerIds.get(p))
      .filter((id) => typeof id !== 'undefined');
    await putJson(baseUrl, `/api/videos/${created.id}`, {
      title: scene.title,
      studioId: linkedStudio,
      performerIds: linkedPerformers,
    });

    result.set(scene.id, {
      coveVideoId: created.id,
      whisparrState: scene.whisparrState,
      studioId: linkedStudio,
      performerIds: linkedPerformers,
    });
  }

  return result;
}

// The endpoint the extension resolves a Cove entity's outward id by (WhisparrOptions.IdentityEndpoint):
// StashDB on v3, ThePornDB on v2. A Cove VideoRemoteId is matched to the connected version by this
// endpoint, so the seeded id must be stored under the exact string the extension filters on. These are
// the extension's own defaults (see WhisparrOptions.StashDbEndpoint / TpdbEndpoint) — setup.mjs pins the
// same values in the options POST so the two agree regardless of any future default drift.
export const IDENTITY_ENDPOINTS = {
  v3: 'https://stashdb.org/graphql',
  v2: 'https://theporndb.net/graphql',
};

/**
 * Attaches the allowlist's real metadata-source id to the seeded Cove videos on the version-correct
 * endpoint, so an outward push resolves the connected version's id (v3 → StashDB id, v2 → TPDB id).
 *
 * Content-safety exception: the allowlist is the suite's small set of real, SFW metadata-source
 * ids (never images, never explicit text — see fixtures/allowlist/README.md). It exists because Whisparr
 * validates every outward id against its metadata source, so a fully-synthetic id resolves to zero rows.
 *
 * On v3 one seeded video is reserved for the per-scene identity: it carries the scene's StashDB id
 * (`kind: "scene"` in the allowlist) instead of the studio's, so the owned-scene push path
 * (`/scene-add`) resolves a real per-scene movie by StashDB id — a distinct identity from the studio
 * every other video carries. v2 has no per-scene identity, so nothing is reserved there.
 *
 * @param {{ baseUrl: string, version: 'v2'|'v3', seeded: Awaited<ReturnType<typeof seedCorpus>> }} args
 * @returns {Promise<{ endpoint: string, remoteId: string, videoIds: number[], sceneRemoteId?: string, sceneVideoId?: number }>}
 *   the endpoint + studio id and the Cove video ids it was attached to, plus (v3) the per-scene StashDB
 *   id and the one Cove video reserved to carry it.
 */
export async function attachAllowlistRemoteIds({ baseUrl, version, seeded }) {
  const file = version === 'v2' ? 'v2-tpdb.json' : 'v3-stashdb.json';
  const allowlist = JSON.parse(readFileSync(join(FIXTURES_DIR, 'allowlist', file), 'utf8'));
  const endpoint = IDENTITY_ENDPOINTS[version] ?? IDENTITY_ENDPOINTS.v3;

  // The primary monitorable identity: the v3 studio's StashDB UUID / the v2 site's ThePornDB id.
  const primary = allowlist.entries.find((e) => e.stashId ?? e.tpdbId);
  if (!primary) {
    throw new Error(`attachAllowlistRemoteIds: no id-bearing entry in allowlist/${file}`);
  }
  const remoteId = version === 'v2' ? String(primary.tpdbId) : String(primary.stashId);

  // The per-scene identity (v3 only): a single allowlisted scene resolvable as a Whisparr movie by its
  // StashDB scene id. Reserve one seeded video to carry it so its StashIds[0] is unambiguously the scene id.
  const sceneEntry = version === 'v3' ? allowlist.entries.find((e) => e.kind === 'scene' && e.stashId) : null;
  // The v3 performer identity (v3 only — v2 has no performer entity): a single allowlisted performer
  // resolvable as a Whisparr performer by its StashDB id, for the performer-monitor proof.
  const performerEntry = version === 'v3' ? allowlist.entries.find((e) => e.kind === 'performer' && e.stashId) : null;
  const videoEntries = [...seeded.values()];
  const reserved = sceneEntry && videoEntries.length > 1 ? videoEntries[videoEntries.length - 1] : null;

  const videoIds = [];
  for (const entry of videoEntries) {
    if (reserved && entry === reserved) {
      continue;
    }
    const ok = await putJson(baseUrl, `/api/videos/${entry.coveVideoId}`, {
      RemoteIds: [{ Endpoint: endpoint, RemoteId: remoteId }],
    });
    if (ok) videoIds.push(entry.coveVideoId);
  }

  let sceneRemoteId;
  let sceneVideoId;
  if (reserved) {
    sceneRemoteId = String(sceneEntry.stashId);
    const ok = await putJson(baseUrl, `/api/videos/${reserved.coveVideoId}`, {
      RemoteIds: [{ Endpoint: endpoint, RemoteId: sceneRemoteId }],
    });
    if (ok) sceneVideoId = reserved.coveVideoId;
  }

  const performerRemoteId = performerEntry ? String(performerEntry.stashId) : undefined;
  return { endpoint, remoteId, videoIds, sceneRemoteId, sceneVideoId, performerRemoteId };
}

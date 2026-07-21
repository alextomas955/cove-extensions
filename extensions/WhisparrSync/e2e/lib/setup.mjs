// Playwright-free bring-up for the offline correctness tier. It boots the whole stack — Cove +
// the SkyHook replay stub + a version-parameterized Whisparr container — configures the extension, and
// seeds the allowlist identities, with a single call and no browser and no secret. The node:test
// correctness specs drive it through before/after hooks.
//
// It imports ONLY the shared harness by package subpath (`@cove-extensions/e2e/harness`), never
// `@cove-extensions/e2e` itself — the package root pulls in Playwright's test runner, and a second
// Playwright instance in the node:test path would break Playwright's module singleton. The plain `api`
// helper below mirrors the shared Playwright `api` fixture's shape so specs need no fixture at all.
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { startHarness } from '@cove-extensions/e2e/harness';
import { resolveExtensionPaths } from '@cove-extensions/e2e/resolve-extension';
import { startSkyHookStub } from './skyhook-stub.mjs';
import { startWhisparr } from './whisparr-container.mjs';
import { startQBittorrent } from './qbittorrent-container.mjs';
import { startFakeIndexer } from './fake-indexer.mjs';
import { provisionPipeline } from './pipeline.mjs';
import { seedCorpus, attachAllowlistRemoteIds, IDENTITY_ENDPOINTS } from './seed-fixtures.mjs';

const HERE = dirname(fileURLToPath(import.meta.url)); // …/extensions/WhisparrSync/e2e/lib
// A committed tiny (~6KB) but 3-minute valid MP4 — long enough to clear Whisparr's sample-runtime floor
// (a ~0s file is always classified a Sample and never imports), small enough to stay hermetic.
const FIXTURE_MEDIA = join(HERE, '..', 'fixtures', 'media', 'pipeline-media.mp4');

/**
 * Creates a dedicated Docker named volume for the acquire→import pipeline, shared by Whisparr + qBit
 * so Whisparr imports qBit's completed download by hardlink (same filesystem). Cove's compose `/data`
 * is the container's ephemeral layer (not a shareable volume), so the pipeline uses its own volume for
 * the acquisition chain; the "Cove physically sees the file" leg would need a shared-`/data` compose
 * change (tracked in PIPELINE-VALIDATION.md). Returns the volume name; caller removes it on teardown.
 */
function createPipelineVolume(projectTag) {
  const name = `wsync-pipeline-${projectTag}`;
  execFileSync('docker', ['volume', 'create', name], { encoding: 'utf8' });
  return name;
}

function removeVolume(name) {
  try {
    execFileSync('docker', ['volume', 'rm', '-f', name], { encoding: 'utf8' });
  } catch {
    // Best-effort: a volume still referenced by a not-yet-reaped container is cleaned by Ryuk anyway.
  }
}

export const EXTENSION_ID = 'com.alextomas955.whisparrsync';

// Resolved self-relatively from this file's location (…/extensions/WhisparrSync/e2e/lib) — the same
// build outputs the Playwright fixtures use, without importing the Playwright-bound fixtures module.
const WHISPARRSYNC_EXTENSION = resolveExtensionPaths(import.meta.url, {
  srcProject: 'WhisparrSync',
  uiProject: 'WhisparrSync.Ui',
});

/** A tiny fetch-based API helper (get/post/put/delete → { status, ok, json, text }) local to setup.mjs. */
function makeApi(baseUrl) {
  async function call(method, path, body) {
    const res = await fetch(`${baseUrl}${path}`, {
      method,
      headers: body ? { 'Content-Type': 'application/json' } : undefined,
      body: body ? JSON.stringify(body) : undefined,
    });
    const text = await res.text();
    let json;
    try {
      json = text ? JSON.parse(text) : undefined;
    } catch {
      json = undefined;
    }
    return { status: res.status, ok: res.ok, json, text };
  }
  return {
    get: (path) => call('GET', path),
    post: (path, body) => call('POST', path, body),
    put: (path, body) => call('PUT', path, body),
    delete: (path) => call('DELETE', path),
  };
}

/** Reads the running instance's first root-folder + quality-profile ids for a usable outward-add config. */
async function resolveAddTargets(whisparr) {
  const headers = { 'X-Api-Key': whisparr.apiKey };
  async function firstId(path) {
    const res = await fetch(`${whisparr.baseUrlFromHost}${path}`, { headers });
    const rows = res.ok ? await res.json() : [];
    return Array.isArray(rows) && rows.length > 0 ? rows[0].id : 0;
  }
  const [rootFolderId, qualityProfileId] = await Promise.all([
    firstId('/api/v3/rootfolder'),
    firstId('/api/v3/qualityprofile'),
  ]);
  return { rootFolderId, qualityProfileId };
}

/**
 * Boots Cove + the SkyHook stub + a Whisparr container (version by parameter), seeds the synthetic
 * corpus plus the allowlist identities, and points the extension at the container.
 *
 * @param {{ version?: 'v2'|'v3' }} opts
 * @returns {Promise<{ harness, whisparr, stub, api, baseUrl: string, seeded, remoteIds, stop: () => Promise<void> }>}
 */
export async function startWhisparrSyncHarness({ version = 'v3', pipeline = false } = {}) {
  const harness = await startHarness();
  let stub;
  let whisparr;
  let qbit;
  let fakeIndexer;
  let dataVolume;
  try {
    // Cove's frontend hard-gates the app behind a first-run wizard until an owner exists; the API-only
    // path here still needs the owner row present (it lives in Postgres, unaffected by the install restart).
    await harness.bootstrapOwner();
    await harness.installExtension(WHISPARRSYNC_EXTENSION);
    const api = makeApi(harness.baseUrl);

    const networks =
      typeof harness.container.getNetworkNames === 'function' ? harness.container.getNetworkNames() : [];
    if (networks.length === 0) {
      throw new Error('startWhisparrSyncHarness: could not resolve the harness Docker network from the Cove container');
    }
    const networkName = networks[0];

    stub = await startSkyHookStub({ networkName });

    // Pipeline mode shares a dedicated /data volume between Whisparr + qBit so a grab is imported by
    // hardlink. Create it before starting Whisparr so both mount the same volume.
    dataVolume = pipeline ? createPipelineVolume(String(harness.containerId).slice(0, 12)) : undefined;

    whisparr = await startWhisparr({ networkName, version, metadataUrl: stub.urlFromWhisparr, dataVolume });

    if (pipeline) {
      qbit = await startQBittorrent({ networkName, dataVolume, dataMount: '/data', downloadDir: '/data/downloads' });
      fakeIndexer = await startFakeIndexer({ networkName, mediaHostPath: FIXTURE_MEDIA });
    }

    const seeded = await seedCorpus({ container: harness.container, baseUrl: harness.baseUrl });
    const remoteIds = await attachAllowlistRemoteIds({ baseUrl: harness.baseUrl, version, seeded });

    // Resolve the instance's REAL root-folder + quality-profile ids before storing options: an outward add
    // (monitor / scene-add) maps the stored RootFolderId to a live root path, and a placeholder id 0 has no
    // matching row, so the add classifies "configured root folder 0 not found" as unreachable (502). Read
    // the ids out of band from Whisparr's own API (both versions expose the Sonarr-shaped /api/v3 surface).
    const { rootFolderId, qualityProfileId } = await resolveAddTargets(whisparr);

    await api.post(`/api/extensions/${EXTENSION_ID}/options`, {
      BaseUrl: whisparr.baseUrlFromCove,
      ApiKey: whisparr.apiKey,
      SelectedVersion: version,
      // Pin the identity endpoints to the exact strings seed-fixtures attached, so the extension's
      // endpoint filter matches the seeded VideoRemoteIds regardless of any future default drift.
      StashDbEndpoint: IDENTITY_ENDPOINTS.v3,
      TpdbEndpoint: IDENTITY_ENDPOINTS.v2,
      RootFolderId: rootFolderId,
      QualityProfileId: qualityProfileId,
      TagsOnAdd: ['cove'],
      MonitorNewByDefault: true,
      AllowQualityUpgrades: false,
    });

    let provisioned;
    if (pipeline) {
      // Wire Whisparr's indexer + download client + relax sample/size gates. The On-Import webhook is
      // registered by the spec (via the shared-network `cove:5073` alias) so it can assert the ingest.
      provisioned = await provisionPipeline({ whisparr, fakeIndexer, qbit, registerWebhook: false });
    }

    const localStub = stub;
    const localWhisparr = whisparr;
    const localQbit = qbit;
    const localFakeIndexer = fakeIndexer;
    return {
      harness,
      whisparr,
      stub,
      qbit,
      fakeIndexer,
      provisioned,
      api,
      baseUrl: harness.baseUrl,
      seeded,
      remoteIds,
      async stop() {
        // Reverse dependency order, best-effort so one failed teardown can't strand the others.
        await localFakeIndexer?.stop().catch(() => {});
        await localQbit?.stop().catch(() => {});
        await localWhisparr.stop().catch(() => {});
        await localStub.stop().catch(() => {});
        await harness.stop().catch(() => {});
        if (dataVolume) {
          removeVolume(dataVolume); // after the containers referencing it are gone
        }
      },
    };
  } catch (err) {
    await fakeIndexer?.stop().catch(() => {});
    await qbit?.stop().catch(() => {});
    await whisparr?.stop().catch(() => {});
    await stub?.stop().catch(() => {});
    await harness.stop().catch(() => {});
    if (dataVolume) {
      removeVolume(dataVolume);
    }
    throw err;
  }
}

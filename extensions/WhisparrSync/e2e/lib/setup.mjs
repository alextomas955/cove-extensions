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
// A committed small (~85KB) but 3-minute valid MP4 — long enough to clear Whisparr's sample-runtime floor
// (a ~0s file is always classified a Sample and never imports), small enough to stay hermetic.
const FIXTURE_MEDIA = join(HERE, '..', 'fixtures', 'media', 'pipeline-media.mp4');

/**
 * Resolves the Docker volume backing the Cove container's `/data`, so the acquire→import pipeline can
 * mount the SAME volume into Whisparr and qBittorrent.
 *
 * Sharing it is the whole point: `/data` is Cove's media root, Whisparr imports qBit's completed
 * download by hardlink into `/data/media/...`, and Cove must then be able to open that file. Giving the
 * pipeline its OWN volume satisfies the hardlink (Whisparr and qBit agree) while leaving Cove unable to
 * see the result, which surfaces as `pathNotVisible` — "Whisparr reported an imported file at a path
 * Cove cannot open" — and fails every On-Import round-trip. The compose file declares the volume, so
 * this only discovers it; compose owns its lifecycle and teardown removes it.
 *
 * @returns {string} the volume name.
 * @throws when the Cove container has no volume-backed `/data` — an unshareable path is exactly the
 * condition this exists to prevent, so it must fail loudly rather than fall back to a private volume.
 */
function resolveCoveDataVolume(containerId) {
  const raw = execFileSync(
    'docker',
    ['inspect', '--format', '{{range .Mounts}}{{.Type}}\t{{.Name}}\t{{.Destination}}\n{{end}}', containerId],
    { encoding: 'utf8' },
  );
  const dataMount = raw
    .split('\n')
    .map((line) => line.split('\t'))
    .find(([type, name, dest]) => dest === '/data' && type === 'volume' && name);
  if (!dataMount) {
    throw new Error(
      'startWhisparrSyncHarness: the Cove container has no named volume at /data, so Whisparr cannot ' +
        'import anywhere Cove can read. Check the cove service\'s volumes in tests/e2e/docker/docker-compose.yml.',
    );
  }
  return dataMount[1];
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

/** Reads the running instance's first root-folder id for a usable outward-add config. */
async function resolveAddTargets(whisparr) {
  const headers = { 'X-Api-Key': whisparr.apiKey };
  const res = await fetch(`${whisparr.baseUrlFromHost}/api/v3/rootfolder`, { headers });
  const rows = res.ok ? await res.json() : [];
  return { rootFolderId: Array.isArray(rows) && rows.length > 0 ? rows[0].id : 0 };
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

    // ALWAYS share Cove's /data volume with Whisparr — not only in pipeline mode. Every On-Import
    // round-trip asserts that Cove can read the file Whisparr reports importing, so a Whisparr with no
    // shared mount writes to its own container layer and the assertion cannot pass by construction.
    // Pipeline mode additionally puts qBit on the same volume, which is what makes the import a hardlink.
    // Resolved before starting Whisparr so every container mounts the same volume.
    dataVolume = resolveCoveDataVolume(harness.containerId);

    whisparr = await startWhisparr({ networkName, version, metadataUrl: stub.urlFromWhisparr, dataVolume });

    if (pipeline) {
      qbit = await startQBittorrent({ networkName, dataVolume, dataMount: '/data', downloadDir: '/data/downloads' });
      fakeIndexer = await startFakeIndexer({ networkName, mediaHostPath: FIXTURE_MEDIA });
    }

    const seeded = await seedCorpus({ container: harness.container, baseUrl: harness.baseUrl });
    const remoteIds = await attachAllowlistRemoteIds({ baseUrl: harness.baseUrl, version, seeded });

    // Resolve the instance's REAL root-folder id before storing options: an outward add (monitor / scene-add)
    // maps the stored RootFolderId to a live root path, and a placeholder id 0 has no matching row, so the add
    // classifies "configured root folder 0 not found" as unreachable (502). Read the id out of band from
    // Whisparr's own API (both versions expose the Sonarr-shaped /api/v3 surface).
    const { rootFolderId } = await resolveAddTargets(whisparr);

    await api.post(`/api/extensions/${EXTENSION_ID}/options`, {
      BaseUrl: whisparr.baseUrlFromCove,
      ApiKey: whisparr.apiKey,
      SelectedVersion: version,
      // Pin the identity endpoints to the exact strings seed-fixtures attached, so the extension's
      // endpoint filter matches the seeded VideoRemoteIds regardless of any future default drift.
      StashDbEndpoint: IDENTITY_ENDPOINTS.v3,
      TpdbEndpoint: IDENTITY_ENDPOINTS.v2,
      RootFolderId: rootFolderId,
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
        // The /data volume belongs to the Cove compose project, so harness.stop() removes it with the
        // rest of the project — this teardown must NOT delete a volume it does not own.
        await harness.stop().catch(() => {});
      },
    };
  } catch (err) {
    await fakeIndexer?.stop().catch(() => {});
    await qbit?.stop().catch(() => {});
    await whisparr?.stop().catch(() => {});
    await stub?.stop().catch(() => {});
    await harness.stop().catch(() => {});
    throw err;
  }
}

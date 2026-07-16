// Playwright-free bring-up for the offline correctness tier. It boots the whole stack — Cove +
// the SkyHook replay stub + a version-parameterized Whisparr container — configures the extension, and
// seeds the allowlist identities, with a single call and no browser and no secret. The node:test
// correctness specs drive it through before/after hooks.
//
// It imports ONLY the shared harness by package subpath (`@cove-extensions/e2e/harness`), never
// `@cove-extensions/e2e` itself — the package root pulls in Playwright's test runner, and a second
// Playwright instance in the node:test path would break Playwright's module singleton. The plain `api`
// helper below mirrors the shared Playwright `api` fixture's shape so specs need no fixture at all.
import { startHarness } from '@cove-extensions/e2e/harness';
import { resolveExtensionPaths } from '@cove-extensions/e2e/resolve-extension';
import { startSkyHookStub } from './skyhook-stub.mjs';
import { startWhisparr } from './whisparr-container.mjs';
import { seedCorpus, attachAllowlistRemoteIds, IDENTITY_ENDPOINTS } from './seed-fixtures.mjs';

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
export async function startWhisparrSyncHarness({ version = 'v3' } = {}) {
  const harness = await startHarness();
  let stub;
  let whisparr;
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
    whisparr = await startWhisparr({ networkName, version, metadataUrl: stub.urlFromWhisparr });

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

    const localStub = stub;
    const localWhisparr = whisparr;
    return {
      harness,
      whisparr,
      stub,
      api,
      baseUrl: harness.baseUrl,
      seeded,
      remoteIds,
      async stop() {
        // Reverse dependency order, best-effort so one failed teardown can't strand the others.
        await localWhisparr.stop().catch(() => {});
        await localStub.stop().catch(() => {});
        await harness.stop().catch(() => {});
      },
    };
  } catch (err) {
    await whisparr?.stop().catch(() => {});
    await stub?.stop().catch(() => {});
    await harness.stop().catch(() => {});
    throw err;
  }
}

// Brings up a real Whisparr container (v3 Eros or v2 Sonarr-shaped, selected by parameter) on the
// harness's Docker network. The Cove container reaches it by its network alias — `http://whisparr:6969`
// — while the test process reaches it on a random mapped host port for the out-of-band API-key read
// and root-folder provisioning.
//
// METADATA OVERRIDE: Whisparr does not call StashDB/ThePornDB directly — every lookup routes
// through its hosted metadata service (api.whisparr.com), configured by a single `<WhisparrMetadata>`
// URL template in /config/config.xml. Pointing that element at the offline replay stub makes identity
// resolution deterministic and secret-free. Whisparr reads config.xml at startup ONLY, so the override
// is written and the container restarted before any lookup. The default template differs per version
// (v3 → api.whisparr.com/v4, v2 → /v3), but the config element and its `{route}` token are identical.
//
// SECRET HANDLING: the API key is read from the RUNNING container's own /config/config.xml
// at startup — never hardcoded, never logged, never committed. Whisparr's default AuthenticationMethod
// is None, so the read `X-Api-Key` header authenticates every subsequent call.
//
// Deliberately hermetic: no indexer and no download client are configured, so nothing can ever grab —
// the live/offline specs assert the extension's OUTWARD wire attribution (origin tag + searchForMovie:
// false), not a real download.
import { GenericContainer, Wait } from 'testcontainers';

// Both v2 and v3 listen on 6969 internally; only the image differs. v2 is Sonarr-shaped (site = series,
// scene = episode) and pins its root folder at /config/media per the v2 outward contract, whereas v3
// provisions /data/media.
const VERSIONS = {
  v3: { image: 'ghcr.io/hotio/whisparr:v3', rootFolder: '/data/media' },
  v2: { image: 'ghcr.io/hotio/whisparr:v2', rootFolder: '/config/media' },
};
const WHISPARR_PORT = 6969;
const ALIAS = 'whisparr';

/** Parses the <ApiKey>…</ApiKey> element out of Whisparr's config.xml. Throws if it is not present yet. */
function parseApiKey(configXml) {
  const match = /<ApiKey>([^<]+)<\/ApiKey>/.exec(configXml);
  if (!match) {
    throw new Error('startWhisparr: could not read <ApiKey> from /config/config.xml');
  }
  return match[1].trim();
}

/**
 * Rewrites the `<WhisparrMetadata>` element to {@link metadataUrl} in place, keeping the `{route}`
 * token. Uses a `|` sed delimiter so the URL's slashes need no escaping, and falls back to inserting
 * the element before `</Config>` on the (not-observed on the pinned builds) chance it is absent.
 */
async function overrideMetadataUrl(container, metadataUrl) {
  const escaped = metadataUrl.replace(/[|&\\]/g, '\\$&');
  const replace = `sed -i 's|<WhisparrMetadata>[^<]*</WhisparrMetadata>|<WhisparrMetadata>${escaped}</WhisparrMetadata>|' /config/config.xml`;
  const insert = `sed -i 's|</Config>|  <WhisparrMetadata>${escaped}</WhisparrMetadata>\\n</Config>|' /config/config.xml`;
  await container.exec(
    ['sh', '-c', `grep -q '<WhisparrMetadata>' /config/config.xml && ${replace} || ${insert}`],
    { user: 'root' },
  );
}

async function waitForPing(baseUrlFromHost, { timeoutMs = 120_000, intervalMs = 1000 } = {}) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const res = await fetch(`${baseUrlFromHost}/ping`).catch(() => null);
    if (res?.ok) return;
    await new Promise((r) => setTimeout(r, intervalMs));
  }
  throw new Error(`startWhisparr: container did not answer /ping within ${timeoutMs}ms at ${baseUrlFromHost}`);
}

/**
 * Starts a Whisparr container joined to {@link networkName} under the alias `whisparr`, optionally
 * overrides its metadata-source URL to a local stub, reads its API key from the running config, and
 * provisions a single root folder so the settings dropdowns (CONN-05) and the add path have a real
 * target. Returns a handle for the specs + a `stop()` teardown.
 *
 * @param {{ networkName: string, version?: 'v2'|'v3', metadataUrl?: string }} opts
 *   - `networkName` the shared harness Docker network, so Cove can resolve `http://whisparr:6969`.
 *   - `version` selects the image + root-folder path (default `v3`).
 *   - `metadataUrl` the `<WhisparrMetadata>` URL template (e.g. from the SkyHook stub). When set, the
 *     config is rewritten and the container restarted so lookups resolve offline at the stub.
 */
export async function startWhisparr({ networkName, version = 'v3', metadataUrl, dataVolume } = {}) {
  const spec = VERSIONS[version];
  if (!spec) {
    throw new Error(`startWhisparr: unsupported version "${version}" (expected 'v2' or 'v3')`);
  }

  // Pipeline mode (import→Cove): share a named `/data` volume with qBit + Cove so Whisparr imports the
  // completed download by HARDLINK (same filesystem) into its root folder, and Cove sees the result. The
  // volume name is a runtime-discovered Cove compose volume; a named volume as a bind source is valid.
  let builder = new GenericContainer(spec.image)
    .withExposedPorts(WHISPARR_PORT)
    .withNetworkMode(networkName)
    .withNetworkAliases(ALIAS)
    .withEnvironment({ PUID: '501', PGID: '20', TZ: 'Etc/UTC' })
    .withWaitStrategy(Wait.forHttp('/ping', WHISPARR_PORT).forStatusCode(200))
    .withStartupTimeout(120_000);
  if (dataVolume) {
    builder = builder.withBindMounts([{ source: dataVolume, target: '/data', mode: 'rw' }]);
  }
  const container = await builder.start();

  // The key lives only in the running container's config — read it out of band (never from a committed value).
  const { output } = await container.exec(['cat', '/config/config.xml']);
  const apiKey = parseApiKey(output);

  if (metadataUrl) {
    await overrideMetadataUrl(container, metadataUrl);
    // config.xml is read at startup only, so the override needs a restart to take effect.
    await container.restart();
  }

  // A restart on an ephemeral host port can reassign a NEW mapped port — read the container's own view
  // of itself after any restart rather than caching a pre-restart port.
  const host = container.getHost();
  const port = container.getMappedPort(WHISPARR_PORT);
  const baseUrlFromHost = `http://${host}:${port}`;
  if (metadataUrl) {
    await waitForPing(baseUrlFromHost);
  }

  // Provision a root folder so the settings dropdowns (CONN-05) and the add path have a real target. The
  // directory is created + owned inside the container first, then registered through the (Sonarr-shaped)
  // v3 API surface both versions expose.
  await container.exec(['mkdir', '-p', spec.rootFolder], { user: 'root' });
  // The hotio images run the Whisparr service as the `hotio` user, and Whisparr's FolderWritableValidator
  // rejects a root folder the service user cannot write (400 FolderWritableValidator). The dir is created
  // as root, so hand it to `hotio` or the registration below silently fails and the root list stays empty.
  await container.exec(['chown', '-R', 'hotio:hotio', spec.rootFolder], { user: 'root' }).catch(() => {});
  if (dataVolume) {
    // The shared volume is written by three services running as different uids (Cove, Whisparr's hotio,
    // qBit's 1655). Make /data + the download dir group/other-permissive so Whisparr can read qBit's
    // completed file and hardlink it into the root folder regardless of which uid created it.
    await container.exec(['mkdir', '-p', '/data/downloads'], { user: 'root' }).catch(() => {});
    await container.exec(['chmod', '-R', '0777', '/data'], { user: 'root' }).catch(() => {});
  }
  await fetch(`${baseUrlFromHost}/api/v3/rootfolder`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Api-Key': apiKey },
    body: JSON.stringify({ path: spec.rootFolder }),
  }).catch(() => {
    // A pre-existing root (or an image that seeds one) is fine — the spec asserts the list is non-empty.
  });

  return {
    container,
    apiKey,
    version,
    alias: ALIAS,
    port: WHISPARR_PORT,
    rootFolder: spec.rootFolder,
    /** The base URL the COVE container uses to reach Whisparr over the shared network. */
    baseUrlFromCove: `http://${ALIAS}:${WHISPARR_PORT}`,
    /** The base URL the TEST process uses to reach Whisparr on its mapped host port. */
    baseUrlFromHost,
    async stop() {
      await container.stop();
    },
  };
}

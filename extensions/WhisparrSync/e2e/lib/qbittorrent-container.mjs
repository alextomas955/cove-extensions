// Brings up a qBittorrent container on the harness's Docker network so a grab can flow all the way to a
// real download client: indexer → Whisparr grab → qBittorrent → Whisparr import → Cove. Whisparr reaches
// it by its network alias (`http://qbittorrent:8080`); the test process reaches it on a random mapped host
// port for out-of-band setup + assertions.
//
// VERSION PIN (do not bump blindly): Whisparr 3.3.4 (Radarr-v4 era) speaks the qBittorrent WebUI API via
// `QBittorrentProxyV2`, which is INCOMPATIBLE with qBittorrent v5.x — v5 changed the add-torrent response
// handling so Whisparr mis-reads a successful add as "Failed to connect to qBittorrent" even though the
// torrent reached qBit. Pin a 4.6.x image. See the whisparr-qbit-version-compat note.
//
// AUTH: a test download client needs no real login. The config is pre-seeded (before qBit's first start,
// since qBit rewrites qBittorrent.conf on exit) with WebUI auth bypassed for the whole Docker subnet, so
// Whisparr connects without credentials and the assertions can drive the WebUI API directly.
//
// SHARED STORAGE: qBit's completed-download dir and Whisparr's import root are the SAME host directory
// (bind-mounted into both), so Whisparr imports by hardlink exactly as in production — no copy, no move
// across volumes. The caller passes the shared host dir; qBit writes completed files under
// `<downloadDir>/whisparr`.
import { GenericContainer, Wait } from 'testcontainers';

const IMAGE = process.env.QBIT_IMAGE ?? 'ghcr.io/linuxserver/qbittorrent:4.6.7';
const QBIT_PORT = 8080;
const ALIAS = 'qbittorrent';
const CATEGORY = 'whisparr';

// Pre-seeded qBittorrent.conf: WebUI on 8080, auth bypassed for any subnet, CSRF/host-header validation
// off (the WebUI is reached by container alias + a mapped host port, neither of which matches qBit's
// default same-origin expectation). Written before first start so qBit never generates a random password.
function configFileContent(downloadDirInContainer) {
  return [
    '[BitTorrent]',
    'Session\\DefaultSavePath=' + downloadDirInContainer,
    'Session\\TempPathEnabled=false',
    '',
    '[Preferences]',
    'WebUI\\Address=*',
    'WebUI\\Port=' + QBIT_PORT,
    'WebUI\\LocalHostAuth=false',
    'WebUI\\AuthSubnetWhitelistEnabled=true',
    'WebUI\\AuthSubnetWhitelist=0.0.0.0/0',
    'WebUI\\CSRFProtection=false',
    'WebUI\\HostHeaderValidation=false',
    'Downloads\\SavePath=' + downloadDirInContainer,
    '',
  ].join('\n');
}

/**
 * Starts qBittorrent joined to {@link networkName} under the alias `qbittorrent`, mounting the shared
 * {@link dataVolume} at `/data` (the SAME mount + path Whisparr uses) and downloading into
 * `/data/downloads` — so Whisparr sees qBit's completed file at the exact path qBit reports and imports
 * it by hardlink. Auth is pre-bypassed. Returns a handle for the specs + a `stop()` teardown.
 *
 * @param {{ networkName: string, dataVolume: string, dataMount?: string, downloadDir?: string }} opts
 * @returns {Promise<{ urlFromHost: string, urlFromWhisparr: string, category: string,
 *   apiFromHost: (path: string, init?: object) => Promise<Response>, stop: () => Promise<void> }>}
 */
export async function startQBittorrent({ networkName, dataVolume, dataMount = '/data', downloadDir = '/data/downloads' }) {
  if (!networkName) {
    throw new Error('startQBittorrent: networkName is required');
  }
  if (!dataVolume) {
    throw new Error('startQBittorrent: dataVolume is required (shared with Whisparr for hardlink import)');
  }

  const container = await new GenericContainer(IMAGE)
    .withExposedPorts(QBIT_PORT)
    .withNetworkMode(networkName)
    .withNetworkAliases(ALIAS)
    .withEnvironment({ PUID: '1655', PGID: '1655', TZ: 'Etc/UTC', WEBUI_PORT: String(QBIT_PORT) })
    // Mount the shared volume at /data (same as Whisparr) so /data/downloads resolves to the SAME files.
    .withBindMounts([{ source: dataVolume, target: dataMount, mode: 'rw' }])
    .withCopyContentToContainer([
      { content: configFileContent(downloadDir), target: '/config/qBittorrent/qBittorrent.conf' },
    ])
    .withWaitStrategy(Wait.forHttp('/api/v2/app/version', QBIT_PORT).forStatusCode(200))
    .withStartupTimeout(120_000)
    .start();

  const host = container.getHost();
  const port = container.getMappedPort(QBIT_PORT);
  const urlFromHost = `http://${host}:${port}`;

  // Create the `whisparr` category so a grab assigned to it lands in the shared download dir.
  await fetch(`${urlFromHost}/api/v2/torrents/createCategory`, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ category: CATEGORY, savePath: downloadDir }),
  }).catch(() => {});

  return {
    urlFromHost,
    urlFromWhisparr: `http://${ALIAS}:${QBIT_PORT}`,
    category: CATEGORY,
    apiFromHost: (path, init) => fetch(`${urlFromHost}${path}`, init),
    stop: () => container.stop(),
  };
}

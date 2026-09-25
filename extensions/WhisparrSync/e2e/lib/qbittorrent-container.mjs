// A real torrent engine, so a grab flows all the way: indexer, grab, download, import, Cove.
//
// Whisparr reaches it by its network alias; the test process reaches it on a mapped host port for
// setup and assertions.
//
// VERSION PIN, DO NOT BUMP BLINDLY. This generation speaks the qBittorrent WebUI API through
// `QBittorrentProxyV2`, which is incompatible with qBittorrent 5.x: v5 changed the add-torrent
// response, and Whisparr reads a successful add as "Failed to connect to qBittorrent" even though the
// torrent reached qBit. Stay on 4.6.x.
//
// AUTH. A test download client needs no real login, and the first run of an unconfigured qBit mints a
// random password into its log. The config is written before the first start instead, with WebUI auth
// bypassed for any subnet, so there is no password to read and no dance to lose. qBit rewrites this
// file on exit, so seeding it afterwards would be overwritten.
//
// SHARED STORAGE. The completed-download directory and Whisparr's import root are the same volume,
// mounted by both containers, so Whisparr imports by hardlink exactly as it would in production. A
// hardlink cannot cross a device, so two copies of a directory would not do.
import { GenericContainer, Wait } from "testcontainers";

const IMAGE = process.env.QBIT_IMAGE ?? "ghcr.io/linuxserver/qbittorrent:4.6.7";
const QBIT_PORT = 8080;
const ALIAS = "qbittorrent";

/** The category a grab is assigned, which is what routes the completed file into the shared path. */
export const QBIT_CATEGORY = "whisparr";

/**
 * The config written before the first start: WebUI on its port, auth bypassed for any subnet, and
 * CSRF and host-header validation off.
 *
 * The last two matter because the WebUI is reached by a container alias from Whisparr and by a mapped
 * host port from the test process, and neither matches qBit's default same-origin expectation.
 */
function configFileContent(downloadDir) {
  return [
    "[BitTorrent]",
    "Session\\DefaultSavePath=" + downloadDir,
    "Session\\TempPathEnabled=false",
    "",
    "[Preferences]",
    "WebUI\\Address=*",
    "WebUI\\Port=" + QBIT_PORT,
    "WebUI\\LocalHostAuth=false",
    "WebUI\\AuthSubnetWhitelistEnabled=true",
    "WebUI\\AuthSubnetWhitelist=0.0.0.0/0",
    "WebUI\\CSRFProtection=false",
    "WebUI\\HostHeaderValidation=false",
    "Downloads\\SavePath=" + downloadDir,
    "",
  ].join("\n");
}

/**
 * Starts qBittorrent on `networkName` under the alias `qbittorrent`, mounting `dataVolume` at
 * `dataMount` and downloading into `downloadDir`.
 *
 * Mount the same volume at the same path Whisparr uses, so qBit's completed file is at the exact path
 * qBit reports and Whisparr imports it by hardlink.
 *
 * @param {{ networkName: string, dataVolume: string, dataMount?: string, downloadDir?: string }} options
 */
export async function startQBittorrent({
  networkName,
  dataVolume,
  dataMount = "/data",
  downloadDir = "/data/downloads",
}) {
  if (!networkName) {
    throw new Error("startQBittorrent: networkName is required");
  }
  if (!dataVolume) {
    throw new Error(
      "startQBittorrent: dataVolume is required - Whisparr imports the completed file by hardlink, which needs one filesystem and not two copies of one.",
    );
  }

  const container = await new GenericContainer(IMAGE)
    .withExposedPorts(QBIT_PORT)
    .withNetworkMode(networkName)
    .withNetworkAliases(ALIAS)
    .withEnvironment({
      PUID: "1000",
      PGID: "1000",
      TZ: "Etc/UTC",
      WEBUI_PORT: String(QBIT_PORT),
    })
    .withBindMounts([{ source: dataVolume, target: dataMount, mode: "rw" }])
    .withCopyContentToContainer([
      {
        content: configFileContent(downloadDir),
        target: "/config/qBittorrent/qBittorrent.conf",
      },
    ])
    .withWaitStrategy(Wait.forHttp("/api/v2/app/version", QBIT_PORT).forStatusCode(200))
    .withStartupTimeout(180_000)
    .start();

  const urlFromHost = `http://${container.getHost()}:${container.getMappedPort(QBIT_PORT)}`;

  // The category has to exist before a grab is assigned to it, or the completed file lands in the
  // default path and Whisparr looks for it where the grab said it would be.
  const created = await fetch(`${urlFromHost}/api/v2/torrents/createCategory`, {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ category: QBIT_CATEGORY, savePath: downloadDir }),
  });
  if (!created.ok) {
    throw new Error(
      `startQBittorrent: creating the "${QBIT_CATEGORY}" category answered ${created.status}. Without it a grab completes somewhere Whisparr does not look.`,
    );
  }

  return {
    urlFromHost,
    urlFromWhisparr: `http://${ALIAS}:${QBIT_PORT}`,
    category: QBIT_CATEGORY,
    downloadDir,

    /** Every torrent qBit holds, as its own API reports them. */
    async torrents() {
      const answered = await fetch(`${urlFromHost}/api/v2/torrents/info`);
      return answered.ok ? answered.json() : [];
    },

    stop: () => container.stop(),
  };
}

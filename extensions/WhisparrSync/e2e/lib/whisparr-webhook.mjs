// Fires a GENUINE Whisparr On-Import at Cove's inbound webhook and wires the authentication for it.
// The whole point is that Whisparr's own event system emits the webhook — the payload shape is
// 100% real, never a hand-posted synthesis — so the assertion proves Cove truly hears and ingests it.
//
// Version-aware so the v2 round-trip can reuse it with version:'v2': the outward container API is the same
// Sonarr-shaped /api/v3 surface on both builds, but the identity a real import hangs off differs
// (v3/Radarr = movie + ManualImport by movieId; v2/Sonarr = series+episode + ManualImport by episodeId),
// and so does the webhook auth channel (see registerWebhookNotification).
import { GenericContainer, Wait } from "testcontainers";

// A clean release-shaped name so Whisparr parses a real quality (WEBDL-1080p) and manualimport lists it.
const IMPORT_FILE_NAME = "Cove.Roundtrip.2020.1080p.WEBDL.mkv";
// A distinct v2 name so a warm container's two imports never collide on one on-disk file.
const V2_IMPORT_FILE_NAME = "Cove.V2.Roundtrip.2020.1080p.WEBDL.mkv";
// The real allowlist ThePornDB site id (Tushy), resolved offline through the SkyHook stub's /site/{tpdbId}.
const V2_SITE_TPDB_ID = 3417;

async function whisparrApi(whisparr, method, path, body) {
  const res = await fetch(`${whisparr.baseUrlFromHost}${path}`, {
    method,
    headers: {
      "X-Api-Key": whisparr.apiKey,
      ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
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

/**
 * Registers a real Webhook notification in Whisparr, pointed at {@link coveWebhookUrl}, that fires on
 * import (eventType Download — the event the extension's receiver ingests).
 *
 * The auth channel is version-aware (the webhook-header capability is captured live in
 * fixtures/skyhook/README.md): the v3 (eros) build exposes the notification `headers` field, so Cove's
 * X-Cove-Token rides directly on the notification; the v2 build's Webhook has no custom-header field, so
 * the token is injected in transit by {@link startTokenShim} instead and {@link coveWebhookUrl} points at
 * the shim (the caller wires this).
 * Either way the fired event and its payload stay real and Cove's token gate is never disabled.
 *
 * @param {{ whisparr, version: 'v2'|'v3', coveWebhookUrl: string, token: string }} opts
 * @returns {Promise<number>} the created notification id.
 */
export async function registerWebhookNotification({ whisparr, version, coveWebhookUrl, token }) {
  const fields = [
    { name: "url", value: coveWebhookUrl },
    { name: "method", value: 1 }, // 1 = POST, mirroring the extension's own auto-register.
  ];
  // v3 carries the shared secret on the custom header directly; v2 relies on the in-transit shim.
  if (version === "v3") {
    fields.push({ name: "headers", value: [{ key: "X-Cove-Token", value: token }] });
  }

  const res = await whisparrApi(whisparr, "POST", "/api/v3/notification", {
    name: "Cove Sync Round-trip",
    implementation: "Webhook",
    implementationName: "Webhook",
    configContract: "WebhookSettings",
    onGrab: false,
    onDownload: true,
    onUpgrade: true,
    onRename: false,
    fields,
  });
  if (!res.ok) {
    throw new Error(`registerWebhookNotification(${version}): POST /api/v3/notification failed (${res.status}): ${res.text}`);
  }
  return res.json.id;
}

/**
 * Fires a genuine On-Import event from Whisparr's own event system — no indexer and no download client.
 * For v3 (Radarr-shaped): resolve a real movie through the offline metadata stub, add it (non-grabbing),
 * drop a file into its folder, then issue a targeted ManualImport by movieId. For v2 (Sonarr-shaped): add
 * the real site (series) resolved by its TPDB id, wait for its episodes to load, then move-import a file from
 * a source folder into the site by episodeId. Either way Whisparr's own event system fires the real On-Import
 * webhook (v3 = movie + movieFile; v2 = series + episodes + episodeFile).
 *
 * @param {{ whisparr, version: 'v2'|'v3', term?: string }} opts
 * @returns {Promise<{ moviePath?: string, movieId?: number, seriesId?: number, seriesPath?: string, episodeId?: number, fileName: string }>}
 *   `moviePath` (v3) / `seriesPath` (v2) is the imported item's own folder the round-trip ties the log entry to.
 */
export async function triggerImport({ whisparr, version, term = "Tushy" }) {
  if (version === "v3") {
    return triggerImportV3({ whisparr, term });
  }
  if (version === "v2") {
    return triggerImportV2({ whisparr });
  }
  // Refuse loudly rather than silently no-op, so a mis-versioned call can never masquerade as a round-trip.
  throw new Error(`triggerImport: unsupported version "${version}" (expected 'v2' or 'v3')`);
}

async function triggerImportV3({ whisparr, term }) {
  const lookup = await whisparrApi(whisparr, "GET", `/api/v3/movie/lookup?term=${encodeURIComponent(term)}`);
  if (!lookup.ok || !Array.isArray(lookup.json) || lookup.json.length === 0) {
    throw new Error(`triggerImport(v3): movie lookup for "${term}" returned no rows (${lookup.status}): ${lookup.text}`);
  }
  const candidate = lookup.json.find((m) => Number.isInteger(m.tmdbId) && m.tmdbId > 0) ?? lookup.json[0];

  const profiles = await whisparrApi(whisparr, "GET", "/api/v3/qualityprofile");
  const qualityProfileId = Array.isArray(profiles.json) && profiles.json.length > 0 ? profiles.json[0].id : 1;

  const movie = await addMovie(whisparr, candidate, qualityProfileId);
  const moviePath = movie.path;
  if (!moviePath) {
    throw new Error(`triggerImport(v3): added movie ${movie.id} has no path`);
  }

  await dropFile(whisparr, moviePath, IMPORT_FILE_NAME);

  // ManualImport needs the quality + languages VERBATIM from the manualimport listing — a synthesized
  // quality object does not import (the same rule the extension's own owned-import path relies on).
  const filePath = `${moviePath.replace(/\/$/, "")}/${IMPORT_FILE_NAME}`;
  const row = await resolveManualImportRow(whisparr, moviePath, filePath);

  const cmd = await whisparrApi(whisparr, "POST", "/api/v3/command", {
    name: "ManualImport",
    importMode: "auto",
    files: [
      {
        path: row.path,
        movieId: movie.id,
        quality: row.quality,
        languages: row.languages,
        releaseGroup: "",
      },
    ],
  });
  if (!cmd.ok) {
    throw new Error(`triggerImport(v3): ManualImport command failed (${cmd.status}): ${cmd.text}`);
  }

  return { movieId: movie.id, moviePath, fileName: IMPORT_FILE_NAME };
}

/** Adds the looked-up movie non-grabbing (searchForMovie:false); tolerates an already-added movie. */
async function addMovie(whisparr, candidate, qualityProfileId) {
  const add = await whisparrApi(whisparr, "POST", "/api/v3/movie", {
    ...candidate,
    qualityProfileId,
    rootFolderPath: whisparr.rootFolder,
    monitored: true,
    minimumAvailability: "released",
    addOptions: { searchForMovie: false, monitor: "movieOnly" },
  });
  if (add.ok) {
    return add.json;
  }

  // A re-run against a warm container already has the movie — resolve the existing row by its tmdbId.
  const existing = await whisparrApi(whisparr, "GET", "/api/v3/movie");
  const found = (Array.isArray(existing.json) ? existing.json : []).find((m) => m.tmdbId === candidate.tmdbId);
  if (found) {
    return found;
  }
  throw new Error(`triggerImport(v3): POST /api/v3/movie failed (${add.status}) and no existing row: ${add.text}`);
}

/** Writes a small named file into the movie folder and hands it to the service user so import can read it. */
async function dropFile(whisparr, folder, fileName) {
  const script =
    `mkdir -p "${folder}" && ` +
    `dd if=/dev/zero of="${folder}/${fileName}" bs=1024 count=64 2>/dev/null && ` +
    `chown -R hotio:hotio "${folder}"`;
  const { exitCode, output } = await whisparr.container.exec(["sh", "-c", script], { user: "root" });
  if (exitCode !== 0) {
    throw new Error(`triggerImport(v3): could not stage import file in ${folder}: ${output}`);
  }
}

/** Reads the manualimport listing for the folder and returns the row for our file (its real quality/languages). */
async function resolveManualImportRow(whisparr, folder, filePath) {
  const listing = await whisparrApi(
    whisparr,
    "GET",
    `/api/v3/manualimport?folder=${encodeURIComponent(folder)}&filterExistingFiles=false`,
  );
  const rows = Array.isArray(listing.json) ? listing.json : [];
  const normalize = (p) => (typeof p === "string" ? p.replace(/\\/g, "/").toLowerCase() : "");
  const target = normalize(filePath);
  const row = rows.find((r) => normalize(r.path) === target) ?? rows[0];
  if (!row) {
    throw new Error(`triggerImport(v3): manualimport listed no rows for ${folder} (${listing.status}): ${listing.text}`);
  }
  return row;
}

/**
 * Fires a genuine v2 (Sonarr-shaped) On-Import: add the real Tushy site (series) resolved by its TPDB id
 * through the offline stub, wait for its episodes to populate, then move-import a file from a source folder
 * into the site attached to a real episode. Whisparr emits its real On-Import webhook (series + episodes +
 * episodeFile — structurally distinct from v3's movie + movieFile).
 */
async function triggerImportV2({ whisparr, tpdbId = V2_SITE_TPDB_ID }) {
  const lookup = await whisparrApi(whisparr, "GET", `/api/v3/series/lookup?term=tpdb:${tpdbId}`);
  if (!lookup.ok || !Array.isArray(lookup.json) || lookup.json.length === 0) {
    throw new Error(`triggerImport(v2): series lookup for tpdb:${tpdbId} returned no rows (${lookup.status}): ${lookup.text}`);
  }
  const addable = lookup.json.find((s) => s.tvdbId === tpdbId) ?? lookup.json[0];

  const profiles = await whisparrApi(whisparr, "GET", "/api/v3/qualityprofile");
  const qualityProfileId = Array.isArray(profiles.json) && profiles.json.length > 0 ? profiles.json[0].id : 1;

  const series = await addSeries(whisparr, addable, tpdbId, qualityProfileId);
  const seriesPath = series.path;
  if (!seriesPath) {
    throw new Error(`triggerImport(v2): added series ${series.id} has no path`);
  }

  // A fresh site fetches its episode list asynchronously after the create, so the episodes settle a beat
  // after the add — wait for at least one before targeting the ManualImport at it.
  const episodes = await waitForEpisodes(whisparr, series.id);
  const episodeId = episodes[0].id;

  // CRITICAL (verified live): drop the file in a SEPARATE source folder, NOT the site folder, and import with
  // importMode:"move". Whisparr v2 (Sonarr) only emits the On-Import event + downloadFolderImported history for
  // a genuine folder-import that MOVES the file into the site folder; importing a file already sitting in its
  // destination is a silent in-place link that fires no notification. The source dir sits beside the root
  // folder (rootFolder's parent) so it is writable and clearly outside any monitored root.
  const sourceDir = `${parentDir(whisparr.rootFolder)}/cove-v2-import-src`;
  await dropFile(whisparr, sourceDir, V2_IMPORT_FILE_NAME);

  // ManualImport needs the quality + languages VERBATIM from the manualimport listing — a synthesized quality
  // does not import. The listing carries a name-parse rejection ("Unknown Series" / "Invalid season or
  // episode"); it is moot here because the explicit seriesId + episodeIds override episode matching (the same
  // rule the extension's own owned-import path relies on — V2Adapter.ImportOwnedSceneAsync).
  const filePath = `${sourceDir}/${V2_IMPORT_FILE_NAME}`;
  const row = await resolveManualImportRow(whisparr, sourceDir, filePath);

  const cmd = await whisparrApi(whisparr, "POST", "/api/v3/command", {
    name: "ManualImport",
    importMode: "move",
    files: [
      {
        path: row.path,
        seriesId: series.id,
        episodeIds: [episodeId],
        quality: row.quality,
        languages: row.languages,
        releaseGroup: "",
      },
    ],
  });
  if (!cmd.ok) {
    throw new Error(`triggerImport(v2): ManualImport command failed (${cmd.status}): ${cmd.text}`);
  }

  return { seriesId: series.id, seriesPath, episodeId, fileName: V2_IMPORT_FILE_NAME };
}

/** Adds the looked-up site (series) non-grabbing (searchForMissingEpisodes:false); tolerates an already-added site. */
async function addSeries(whisparr, addable, tpdbId, qualityProfileId) {
  const add = await whisparrApi(whisparr, "POST", "/api/v3/series", {
    ...addable,
    qualityProfileId,
    rootFolderPath: whisparr.rootFolder,
    monitored: true,
    monitorNewItems: "all",
    seasonFolder: true,
    // Loop-safe: register the site + mark episodes wanted WITHOUT grabbing (mirrors V2Adapter.BuildSiteAddBody);
    // the container is hermetic anyway, but the real import below is a ManualImport, never a grab.
    addOptions: { monitor: "all", searchForMissingEpisodes: false, searchForCutoffUnmetEpisodes: false },
  });
  if (add.ok) {
    return add.json;
  }

  // A re-run against a warm container already has the site — v2 answers a duplicate add with a 400
  // SeriesExistsValidator, so resolve the existing row by its tvdbId (the TPDB site id).
  const existing = await whisparrApi(whisparr, "GET", "/api/v3/series");
  const found = (Array.isArray(existing.json) ? existing.json : []).find((s) => s.tvdbId === tpdbId);
  if (found) {
    return found;
  }
  throw new Error(`triggerImport(v2): POST /api/v3/series failed (${add.status}) and no existing row: ${add.text}`);
}

/** The parent of a POSIX-ish container path ("/config/media" -> "/config"); "/" when there is no parent. */
function parentDir(path) {
  const trimmed = path.replace(/\/+$/, "");
  const cut = trimmed.lastIndexOf("/");
  return cut > 0 ? trimmed.slice(0, cut) : "/";
}

/** Polls GET /api/v3/episode?seriesId until the site's episode list has populated (Whisparr loads it async). */
async function waitForEpisodes(whisparr, seriesId, { timeoutMs = 90_000, intervalMs = 1000 } = {}) {
  const deadline = Date.now() + timeoutMs;
  let last;
  while (Date.now() < deadline) {
    const res = await whisparrApi(whisparr, "GET", `/api/v3/episode?seriesId=${seriesId}`);
    last = Array.isArray(res.json) ? res.json : [];
    if (last.length > 0) {
      return last;
    }
    await new Promise((r) => setTimeout(r, intervalMs));
  }
  throw new Error(`triggerImport(v2): series ${seriesId} loaded no episodes within ${timeoutMs}ms`);
}

/**
 * A tiny version-agnostic token-injecting reverse proxy on the shared Docker network, for the v2 path:
 * Whisparr's v2 Webhook cannot send a custom header (see fixtures/skyhook/README.md), so it posts to this
 * shim, which adds X-Cove-Token and forwards the request UNCHANGED to Cove. Only the auth header is added
 * in transit — the event and payload stay real, and Cove's fail-closed token gate is honoured, never bypassed.
 *
 * Unused by the v3 round-trip (v3 uses the header directly); the v2 round-trip drives and verifies it.
 *
 * @param {{ networkName: string, token: string, coveUrl?: string, alias?: string }} opts
 * @returns {Promise<{ alias: string, port: number, urlFromWhisparr: string, stop: () => Promise<void> }>}
 */
export async function startTokenShim({ networkName, token, coveUrl = "http://cove:5073", alias = "cove-token-shim" }) {
  const port = 8080;
  const script = `
const http = require("http");
const target = new URL(process.env.COVE_TARGET);
const tokenValue = process.env.COVE_TOKEN;
http
  .createServer((req, res) => {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => {
      const body = Buffer.concat(chunks);
      const headers = { ...req.headers, "x-cove-token": tokenValue, host: target.host };
      const proxied = http.request(
        { hostname: target.hostname, port: target.port || 80, path: req.url, method: req.method, headers },
        (upstream) => {
          res.writeHead(upstream.statusCode, upstream.headers);
          upstream.pipe(res);
        },
      );
      proxied.on("error", () => {
        res.writeHead(502);
        res.end();
      });
      proxied.end(body);
    });
  })
  .listen(${port}, () => console.log("token-shim listening"));
`;

  const container = await new GenericContainer("node:22-alpine")
    .withNetworkMode(networkName)
    .withNetworkAliases(alias)
    .withExposedPorts(port)
    .withEnvironment({ COVE_TARGET: coveUrl, COVE_TOKEN: token })
    .withCommand(["node", "-e", script])
    .withWaitStrategy(Wait.forLogMessage("token-shim listening"))
    .start();

  return {
    alias,
    port,
    /** The base URL the Whisparr container uses to reach the shim over the shared network. */
    urlFromWhisparr: `http://${alias}:${port}`,
    async stop() {
      await container.stop();
    },
  };
}

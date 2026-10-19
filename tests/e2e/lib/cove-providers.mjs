// Provider configuration for a fixture Cove: lifted from the machine's own native Cove install when
// there is one, and supplied as placeholders when there is not.
//
// That install is a READ SUBJECT, never a write target. `readFileSync` is the only thing this module
// imports from node:fs, so a write from here is impossible rather than merely forbidden, and the
// whole lift lives here so no caller opens that file for itself.
//
// A provider this module cannot find is reported as a named skip in the value it returns. It is
// never an error, and never a reason to change the install it read.
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";

export const CONFIG_FILE = "cove-config.json";

/**
 * Provider entries for a fixture that has no real install to lift from.
 *
 * The keys are synthetic and obviously so, which is what keeps a provider read failing cleanly
 * rather than being absent, and keeps a reader from mistaking one for a lifted credential.
 */
export const PLACEHOLDER_SERVERS = Object.freeze([
  Object.freeze({
    endpoint: "https://stashdb.org/graphql",
    apiKey: "placeholder-not-a-real-stashdb-key",
    name: "stashdb",
    maxRequestsPerMinute: 240,
  }),
  Object.freeze({
    endpoint: "https://theporndb.net/graphql",
    apiKey: "placeholder-not-a-real-theporndb-key",
    name: "ThePornDB",
    maxRequestsPerMinute: 240,
  }),
]);

// ---- Pure helpers: no disk, so the tests drive them with a document rather than an install. ----

/**
 * The directory a native Cove install keeps its data in.
 *
 * Mirrors the host's own rule (`COVE_HOME` when set, else the local application data directory), so
 * setting that variable moves this with it instead of leaving a second guess behind.
 */
export function coveDataRoot() {
  const configured = process.env.COVE_HOME?.trim();
  if (configured) return configured;
  return join(process.env.LOCALAPPDATA ?? "", "cove");
}

/**
 * Reads the metadata-server entries out of a `cove-config.json` document.
 *
 * `names` narrows the result to the providers named, matched case-insensitively; any that the
 * document does not carry are named in `skip`.
 *
 * Never throws. An unreadable document, an absent section and an absent provider all come back as
 * `{ servers: [], skip }` or `{ servers, skip }`, because the caller's answer to all three is to
 * report the reason and carry on.
 *
 * A skip names the FILE and never the directory holding it. A skip is a recordable string, and a
 * record outlives the run and is read by someone who was not here, so where a person keeps their
 * install is not something it may carry.
 *
 * @param {string} text
 * @param {{names?: string[]}} [options]
 * @returns {{servers: object[], skip: string|null}}
 */
export function parseMetadataServers(text, { names } = {}) {
  let document;
  try {
    document = JSON.parse(text);
  } catch (cause) {
    return { servers: [], skip: `${CONFIG_FILE} is not readable as JSON: ${cause.message}` };
  }

  const declared = document?.scraping?.metadataServers;
  if (!Array.isArray(declared)) {
    return { servers: [], skip: `${CONFIG_FILE} declares no scraping.metadataServers` };
  }
  if (names === undefined) return { servers: declared, skip: null };

  const matches = (server, name) => server?.name?.toLowerCase?.() === name.toLowerCase();
  const servers = declared.filter((server) => names.some((name) => matches(server, name)));
  const absent = names.filter((name) => !declared.some((server) => matches(server, name)));
  return {
    servers,
    skip:
      absent.length === 0
        ? null
        : `${CONFIG_FILE} declares no metadata server named ${absent.join(", ")}`,
  };
}

/**
 * The environment that delivers `servers` to a Cove container's configuration.
 *
 * The index is positional and starts at zero, matching the form the harness's compose file already
 * uses for the library paths.
 *
 * @param {object[]} servers
 * @returns {Record<string, string>}
 */
export function providerEnv(servers) {
  const env = {};
  servers.forEach((server, index) => {
    const prefix = `COVE__Scraping__MetadataServers__${index}__`;
    env[`${prefix}Endpoint`] = server.endpoint;
    env[`${prefix}ApiKey`] = server.apiKey;
    env[`${prefix}Name`] = server.name;
    env[`${prefix}MaxRequestsPerMinute`] = String(server.maxRequestsPerMinute ?? 240);
  });
  return env;
}

/** The same environment for {@link PLACEHOLDER_SERVERS}. */
export function placeholderProviderEnv() {
  return providerEnv(PLACEHOLDER_SERVERS);
}

/**
 * The only sanctioned way a server list becomes text.
 *
 * Reports each key's presence and character count and never its characters, so anything derived from
 * this is safe to print, to log and to write into a file that outlives the run. Everything that has
 * something to say about a lifted credential says it through here.
 *
 * @param {object[]} servers
 */
export function describeServers(servers) {
  return servers.map((server) => ({
    endpoint: server.endpoint ?? "",
    name: server.name ?? "",
    maxRequestsPerMinute: server.maxRequestsPerMinute ?? 240,
    apiKey: {
      present: typeof server.apiKey === "string" && server.apiKey.length > 0,
      chars: typeof server.apiKey === "string" ? server.apiKey.length : 0,
    },
  }));
}

// ---- The one read of the install. ----

/**
 * Lifts the metadata-server entries out of the machine's own Cove install.
 *
 * `path` is returned alongside for a caller that wants to print where it looked, and is deliberately
 * absent from `skip`: `skip` is the half a caller records. On a machine with no install at all,
 * which is the ordinary case off this developer's desk, the result is an empty list and a skip
 * naming the file.
 *
 * @param {{names?: string[]}} [options]
 * @returns {{path: string, servers: object[], skip: string|null}}
 */
export function liftMetadataServers({ names } = {}) {
  const path = join(coveDataRoot(), CONFIG_FILE);
  let text;
  try {
    text = readFileSync(path, "utf8");
  } catch (cause) {
    // The code rather than the message: a filesystem error quotes the path it failed on.
    const reason =
      cause.code === "ENOENT"
        ? `no ${CONFIG_FILE} on this machine`
        : `${CONFIG_FILE} could not be read (${cause.code ?? cause.name})`;
    return { path, servers: [], skip: reason };
  }
  return { path, ...parseMetadataServers(text, { names }) };
}

/**
 * How many library roots the machine's own Cove install is configured with.
 *
 * The COUNT and never the paths: a library path names where someone keeps their own media, so it is
 * the one thing here that must not survive into anything written down. Neither the paths nor the
 * file's location are returned at all, which is what keeps a caller from recording either by
 * accident rather than by discipline.
 *
 * @returns {{count: number|null, skip: string|null}}
 */
export function liftLibraryPathCount() {
  let document;
  try {
    document = JSON.parse(readFileSync(join(coveDataRoot(), CONFIG_FILE), "utf8"));
  } catch {
    return { count: null, skip: `this machine has no readable ${CONFIG_FILE}` };
  }
  const declared = document?.covePaths;
  return Array.isArray(declared)
    ? { count: declared.length, skip: null }
    : { count: null, skip: `${CONFIG_FILE} declares no covePaths` };
}

/**
 * A fingerprint of the machine's own Cove configuration file.
 *
 * Taken on both sides of anything that reads the install, so "nothing was written to it" is a
 * comparison a reader can check rather than an assurance. A digest is returned rather than any part
 * of the content, so comparing two of them says only whether the bytes moved.
 *
 * @returns {{sha256: string|null, bytes: number|null, skip: string|null}}
 */
export function installConfigFingerprint() {
  let contents;
  try {
    contents = readFileSync(join(coveDataRoot(), CONFIG_FILE));
  } catch {
    return { sha256: null, bytes: null, skip: `this machine has no readable ${CONFIG_FILE}` };
  }
  return {
    sha256: createHash("sha256").update(contents).digest("hex"),
    bytes: contents.length,
    skip: null,
  };
}

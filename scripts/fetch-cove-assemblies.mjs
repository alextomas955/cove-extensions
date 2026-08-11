// Extracts Cove's published assemblies out of the released `cove-app` container image, so a CI leg
// can compile and run the test project's integration tier against the exact binaries the released
// host loads — rather than against a source build nobody runs, or against a NuGet closure that does
// not exist (Cove.Data is on no feed).
//
// The image reference is READ from Directory.Build.props, never passed in from workflow YAML and
// never written here as a literal: one place declares which Cove CI tests against, and a rename of
// either property fails this script's own node --test rather than drifting silently.
//
// No `docker`, no `curl`, no `tar`: the registry is spoken to over Node's own fetch and the layer is
// gunzipped and untarred in-process, so the script needs no binary on PATH and behaves identically
// on the Windows dev machine and the Linux runner.
//
// The layer carrying /opt/cove is selected BY CONTENT, never by index or size — the amd64 manifest
// lists that layer twice under different digests, so position and size cannot identify it. Descending
// size is only the order the candidates are probed in.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import zlib from "node:zlib";
import { Readable } from "node:stream";
import { pipeline } from "node:stream/promises";

// import.meta.dirname, never a filesystem path read off a module URL's path component: on Windows
// that yields a leading-slash form which resolves to a doubled drive prefix.
const repoRoot = path.resolve(import.meta.dirname, "..");

const DEFAULT_PROPS_PATH = path.join(repoRoot, "Directory.Build.props");
const DEFAULT_OUT_DIR = path.join(repoRoot, "artifacts", "cove-assemblies");

// The one member whose presence defines the layer. It is also what every consumer of the extraction
// needs, so an extraction that does not carry it is a failure rather than a smaller success.
const MARKER_MEMBER = "opt/cove/Cove.Data.dll";
const MEMBER_PREFIX = "opt/cove/";

const TAR_BLOCK = 512;

const MANIFEST_ACCEPT = [
  "application/vnd.oci.image.index.v1+json",
  "application/vnd.oci.image.manifest.v1+json",
  "application/vnd.docker.distribution.manifest.list.v2+json",
  "application/vnd.docker.distribution.manifest.v2+json",
].join(", ");

// ---------------------------------------------------------------------------------------------
// Pure helpers. These carry the logic worth testing, and they touch neither the network nor disk so
// the test file can drive them with fixtures instead of a registry round trip.
// ---------------------------------------------------------------------------------------------

/**
 * Reads the flat `<Name>value</Name>` property elements out of an MSBuild file's text.
 * Mirrors scripts/validate-extension-repo.mjs's reader rather than introducing a second dialect:
 * later declarations win, and a `$(Other)` reference expands from what has already been read.
 */
export function parseMsBuildProperties(content) {
  const props = {};
  const pattern = /<([A-Za-z_][A-Za-z0-9_.-]*)(?:\s+[^>]*)?>([^<]*)<\/\1>/g;
  for (const match of content.matchAll(pattern)) {
    const [, name, rawValue] = match;
    props[name] = rawValue
      .trim()
      .replace(/\$\(([^)]+)\)/g, (_, propertyName) => props[propertyName] ?? `$(${propertyName})`);
  }
  return props;
}

/**
 * Splits `ghcr.io/yourcove/cove-app` into its registry host and repository path.
 * <remarks>
 * The registry host is taken from the reference itself and never from an argument, so the token
 * endpoint and the blob endpoint are always the same host the declared image names. A reference
 * with no host component is rejected rather than defaulted to Docker Hub: this repo declares one
 * image, and guessing a different registry for a malformed value is how a fetch ends up somewhere
 * nobody named.
 * </remarks>
 */
export function splitImageReference(reference) {
  const value = String(reference ?? "").trim();
  if (value === "") throw new Error("The Cove test image repository is empty.");
  if (/[A-Za-z][A-Za-z0-9+.-]*:\/\//.test(value)) {
    throw new Error(
      `The Cove test image repository must be a bare image reference, not a URL: '${value}'.`,
    );
  }
  const slash = value.indexOf("/");
  if (slash <= 0) {
    throw new Error(
      `The Cove test image repository '${value}' names no registry host (expected e.g. ghcr.io/owner/name).`,
    );
  }
  const registry = value.slice(0, slash);
  const repository = value.slice(slash + 1);
  if (!/^[A-Za-z0-9.-]+(?::\d+)?$/.test(registry)) {
    throw new Error(`The Cove test image registry host '${registry}' is not a plain host name.`);
  }
  if (repository === "")
    throw new Error(`The Cove test image reference '${value}' names no repository.`);
  return { registry, repository };
}

/**
 * Orders layer descriptors so the largest is probed first. A speed heuristic only — the caller
 * accepts a layer on the content probe, never on this order.
 */
export function orderLayerCandidates(layers) {
  return [...(layers ?? [])]
    .filter((layer) => typeof layer?.digest === "string" && layer.digest !== "")
    .sort((a, b) => (Number(b.size) || 0) - (Number(a.size) || 0));
}

/**
 * Parses one 512-byte tar header block. Returns null for the all-zero block that ends the archive.
 * <remarks>
 * Understands the ustar `prefix` field and the GNU long-name / pax entry types, because a layer
 * built by any of the common writers has to read the same. Sizes are octal; the rare base-256
 * encoding is rejected loudly rather than silently truncated, since a mis-read size desynchronises
 * every member after it.
 * </remarks>
 */
export function parseTarHeader(block) {
  if (block.length < TAR_BLOCK) throw new Error("A tar header block is shorter than 512 bytes.");
  let allZero = true;
  for (let i = 0; i < TAR_BLOCK; i += 1) {
    if (block[i] !== 0) {
      allZero = false;
      break;
    }
  }
  if (allZero) return null;

  const readString = (start, length) => {
    const slice = block.subarray(start, start + length);
    const end = slice.indexOf(0);
    return slice.subarray(0, end === -1 ? slice.length : end).toString("utf8");
  };

  const sizeField = block.subarray(124, 136);
  if ((sizeField[0] & 0x80) !== 0) {
    throw new Error("A tar member uses base-256 sizes, which this reader does not decode.");
  }
  const sizeText = readString(124, 12).trim();
  const size = sizeText === "" ? 0 : Number.parseInt(sizeText, 8);
  if (!Number.isFinite(size) || size < 0) {
    throw new Error(`A tar member declares an unreadable size '${sizeText}'.`);
  }

  const name = readString(0, 100);
  const prefix = readString(345, 155);
  const type = readString(156, 1) || "0";

  return { name: prefix === "" ? name : `${prefix}/${name}`, size, type };
}

/**
 * Walks a complete tar archive, yielding each regular member as `{ name, body }`.
 * <remarks>
 * Every member's body is padded up to the next 512-byte boundary, and reading that padding as
 * content would desynchronise the archive from the first odd-sized file onward — so the padded
 * length is what advances the cursor and the declared size is what slices the body. GNU long-name
 * entries carry the following member's name and are folded in here rather than surfaced; directory
 * and link entries are skipped, since only file bytes are extracted.
 * </remarks>
 */
export function* readTarMembers(archive) {
  let cursor = 0;
  let longName = null;

  while (cursor + TAR_BLOCK <= archive.length) {
    const header = parseTarHeader(archive.subarray(cursor, cursor + TAR_BLOCK));
    cursor += TAR_BLOCK;
    if (header === null) break;

    const padded = Math.ceil(header.size / TAR_BLOCK) * TAR_BLOCK;
    if (cursor + padded > archive.length) {
      throw new Error(`The archive ends mid-member at '${header.name}'.`);
    }
    const body = archive.subarray(cursor, cursor + header.size);
    cursor += padded;

    if (header.type === "L") {
      const end = body.indexOf(0);
      longName = body.subarray(0, end === -1 ? body.length : end).toString("utf8");
      continue;
    }

    const name = longName ?? header.name;
    longName = null;

    if (header.type === "0" || header.type === "\0" || header.type === "") {
      yield { name, body };
    }
  }
}

/**
 * Maps a tar member name to its path inside the output directory, or null when the member is not
 * part of the extraction.
 * <remarks>
 * Returns a path relative to the output root with the `opt/cove/` prefix removed but any deeper
 * structure kept, so `opt/cove/runtimes/…/x.so` lands under `runtimes/`. Overlay whiteout markers
 * and any member that would climb out of the output directory are refused — a layer is third-party
 * content and its member names are not trusted to stay inside where they are written.
 * </remarks>
 */
export function flattenCoveMemberPath(name) {
  let value = String(name ?? "").replaceAll("\\", "/");
  while (value.startsWith("./")) value = value.slice(2);
  while (value.startsWith("/")) value = value.slice(1);
  if (!value.startsWith(MEMBER_PREFIX)) return null;

  const relative = value.slice(MEMBER_PREFIX.length);
  if (relative === "") return null;

  const segments = relative.split("/").filter((segment) => segment !== "");
  if (segments.length === 0) return null;
  if (segments.some((segment) => segment === "." || segment === "..")) return null;
  if (segments.at(-1).startsWith(".wh.")) return null;

  return segments.join("/");
}

/**
 * Picks the layer that carries the marker member, probing candidates in the given order.
 * <remarks>
 * `probe` is supplied by the caller so the selection logic itself makes no network call: it is
 * handed a descriptor and answers whether that layer carries the marker. The first layer that
 * answers yes wins; an empty answer set is an error naming everything that was searched, never a
 * silent empty extraction.
 * </remarks>
 */
export async function selectLayerByContent(candidates, probe) {
  const searched = [];
  for (const candidate of candidates) {
    searched.push(`${candidate.digest} (${candidate.size ?? "?"} bytes)`);
    // Sequential on purpose: the descending-size order exists so the first probe normally wins, and
    // probing in parallel would download every layer to learn what one answer already settles.
     
    if (await probe(candidate)) return candidate;
  }
  throw new Error(
    `No layer carries ${MARKER_MEMBER}. Searched ${searched.length} layer(s): ${searched.join(", ")}.`,
  );
}

/**
 * Reads the Win32 version-resource strings a .NET assembly carries — `Assembly Version` is the
 * managed assembly version and `ProductVersion` the informational one.
 * <remarks>
 * A diagnostic reader, not the gate: the build's own `GetAssemblyIdentity` task is what asserts the
 * version. Keys and values in the resource are UTF-16LE and 4-byte aligned, so the value is found by
 * stepping over the padding zeros after the key's terminator. A key that is absent yields no entry
 * rather than a guess.
 * </remarks>
 */
export function readVersionStrings(
  buffer,
  keys = ["Assembly Version", "ProductVersion", "FileVersion"],
) {
  const found = {};
  for (const key of keys) {
    const needle = Buffer.from(`${key}\0`, "utf16le");
    const at = buffer.indexOf(needle);
    if (at === -1) continue;

    let cursor = at + needle.length;
    while (cursor + 1 < buffer.length && buffer.readUInt16LE(cursor) === 0) cursor += 2;

    const characters = [];
    while (cursor + 1 < buffer.length && characters.length < 128) {
      const unit = buffer.readUInt16LE(cursor);
      if (unit === 0) break;
      characters.push(unit);
      cursor += 2;
    }
    const value = String.fromCharCode(...characters).trim();
    if (value !== "") found[key] = value;
  }
  return found;
}

// ---------------------------------------------------------------------------------------------
// Registry and extraction. Everything below reaches the network or the disk.
// ---------------------------------------------------------------------------------------------

/** Reads the declared Cove test image reference out of Directory.Build.props. */
export function readCoveImageReference(propsPath = DEFAULT_PROPS_PATH) {
  if (!fs.existsSync(propsPath)) {
    throw new Error(
      `${propsPath} does not exist, so the Cove test image reference cannot be read.`,
    );
  }
  const props = parseMsBuildProperties(fs.readFileSync(propsPath, "utf8"));
  const repository = props.CoveTestImageRepository ?? "";
  const tag = props.CoveTestImageTag ?? "";
  if (repository === "" || tag === "") {
    throw new Error(
      `${propsPath} must declare CoveTestImageRepository and CoveTestImageTag; read repository='${repository}' tag='${tag}'.`,
    );
  }
  return { repository, tag, ...splitImageReference(repository) };
}

async function fetchPullToken(registry, repository) {
  const url = `https://${registry}/token?service=${encodeURIComponent(registry)}&scope=${encodeURIComponent(`repository:${repository}:pull`)}`;
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(`Anonymous pull token request to ${registry} failed with ${response.status}.`);
  }
  const body = await response.json();
  const token = body.token ?? body.access_token;
  if (typeof token !== "string" || token === "") {
    throw new Error(`${registry} returned no pull token for ${repository}.`);
  }
  return token;
}

async function registryGet(registry, repositoryPath, token, accept) {
  const url = `https://${registry}/v2/${repositoryPath}`;
  const response = await fetch(url, {
    headers: { authorization: `Bearer ${token}`, ...(accept ? { accept } : {}) },
    // The registry answers a blob with a redirect to its CDN. `follow` is fetch's default; what
    // matters is that the URL was built from the declared reference rather than taken from input.
  });
  if (!response.ok) {
    throw new Error(`GET ${url} failed with ${response.status} ${response.statusText}.`);
  }
  return response;
}

/** Resolves the tag to the linux/amd64 manifest, returning its digest and layer descriptors. */
async function resolvePlatformManifest(registry, repository, tag, token) {
  const indexResponse = await registryGet(
    registry,
    `${repository}/manifests/${encodeURIComponent(tag)}`,
    token,
    MANIFEST_ACCEPT,
  );
  const indexDigest = indexResponse.headers.get("docker-content-digest") ?? "";
  const index = await indexResponse.json();

  if (Array.isArray(index.manifests)) {
    const amd64 = index.manifests.find(
      (entry) => entry?.platform?.os === "linux" && entry?.platform?.architecture === "amd64",
    );
    if (!amd64) {
      const listed = index.manifests
        .map((entry) => `${entry?.platform?.os ?? "?"}/${entry?.platform?.architecture ?? "?"}`)
        .join(", ");
      throw new Error(`The image index carries no linux/amd64 manifest; it lists: ${listed}.`);
    }
    const manifestResponse = await registryGet(
      registry,
      `${repository}/manifests/${amd64.digest}`,
      token,
      MANIFEST_ACCEPT,
    );
    return { digest: amd64.digest, manifest: await manifestResponse.json() };
  }

  return { digest: indexDigest, manifest: index };
}

/**
 * Streams one layer blob, gunzips it, and hands every `opt/cove/…` member to `onMember`.
 * Returns the set of member paths it saw, so the caller can decide whether this was the right layer.
 */
async function readLayerMembers(registry, repository, token, digest, onMember) {
  const response = await registryGet(registry, `${repository}/blobs/${digest}`, token);
  const seen = new Set();

  const gunzip = zlib.createGunzip();
  const source = Readable.fromWeb(response.body);

  // The whole layer is read before any member is emitted, rather than stopping once the marker
  // appears: a tar gives no guarantee that the opt/cove members are contiguous, and an early exit
  // that happened to be right today would fail by writing FEWER files — silently, which is the exact
  // shape of degrade this extraction exists to make impossible.
  const consume = async () => {
    const chunks = [];
    for await (const chunk of gunzip) chunks.push(chunk);

    for (const { name, body } of readTarMembers(Buffer.concat(chunks))) {
      const relative = flattenCoveMemberPath(name);
      if (relative === null) continue;
      seen.add(relative);
      onMember(relative, body);
    }
  };

  await Promise.all([pipeline(source, gunzip), consume()]);
  return seen;
}

function emptyDirectory(directory) {
  if (!fs.existsSync(directory)) return;
  for (const entry of fs.readdirSync(directory)) {
    fs.rmSync(path.join(directory, entry), { recursive: true, force: true });
  }
}

function parseArguments(argv) {
  let out = DEFAULT_OUT_DIR;
  for (let i = 0; i < argv.length; i += 1) {
    if (argv[i] === "--out") {
      const value = argv[i + 1];
      if (value === undefined) throw new Error("--out needs a directory argument.");
      out = path.resolve(value);
      i += 1;
    } else {
      throw new Error(
        `Unrecognised argument '${argv[i]}'. Usage: fetch-cove-assemblies.mjs [--out <dir>]`,
      );
    }
  }
  return { out };
}

export async function main(argv) {
  const { out } = parseArguments(argv);
  const image = readCoveImageReference();

  console.log(`Cove test image: ${image.registry}/${image.repository}:${image.tag}`);

  const token = await fetchPullToken(image.registry, image.repository);
  const { digest, manifest } = await resolvePlatformManifest(
    image.registry,
    image.repository,
    image.tag,
    token,
  );
  console.log(`manifest digest: ${digest}`);

  const candidates = orderLayerCandidates(manifest.layers);
  if (candidates.length === 0) throw new Error("The resolved manifest lists no layers.");

  fs.mkdirSync(out, { recursive: true });
  emptyDirectory(out);

  let written = 0;
  const probe = async (candidate) => {
    written = 0;
    emptyDirectory(out);
    const seen = await readLayerMembers(
      image.registry,
      image.repository,
      token,
      candidate.digest,
      (relative, body) => {
        const destination = path.join(out, relative);
        fs.mkdirSync(path.dirname(destination), { recursive: true });
        fs.writeFileSync(destination, body);
        written += 1;
      },
    );
    if (seen.has(path.posix.basename(MARKER_MEMBER))) return true;
    // Not this layer: leave nothing behind for the next probe to mistake for its own output.
    emptyDirectory(out);
    written = 0;
    return false;
  };

  const layer = await selectLayerByContent(candidates, probe);
  console.log(`layer: ${layer.digest}`);

  const markerPath = path.join(out, path.posix.basename(MARKER_MEMBER));
  if (written === 0 || !fs.existsSync(markerPath)) {
    throw new Error(
      `The extraction wrote ${written} file(s) and left no ${path.posix.basename(MARKER_MEMBER)} in ${out}. An empty extraction fails here rather than later as a quietly smaller test run.`,
    );
  }

  console.log(`files written: ${written}`);
  const versions = readVersionStrings(fs.readFileSync(markerPath));
  console.log(`Cove.Data.dll assembly version: ${versions["Assembly Version"] ?? "unreadable"}`);
  console.log(`Cove.Data.dll informational version: ${versions.ProductVersion ?? "unreadable"}`);
  console.log(`output directory: ${out}`);
  return 0;
}

if (import.meta.main) {
  main(process.argv.slice(2)).then(
    (code) => {
      process.exitCode = code;
    },
    (error) => {
      console.error(`fetch-cove-assemblies: ${error.message}`);
      process.exitCode = 1;
    },
  );
}

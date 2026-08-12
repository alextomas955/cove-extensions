// Extracts Cove's published assemblies out of the released `cove-app` container image, so a CI leg
// can compile and run the test project's integration tier against the exact binaries the released
// host loads — rather than against a source build nobody runs, or against a NuGet closure that does
// not exist (Cove.Data is on no feed).
//
// The image REPOSITORY is read from Directory.Build.props and is never written here as a literal, so
// a rename of either image property fails this script's own node --test rather than drifting
// silently. The TAG may arrive as --tag, because a CI leg testing a resolved version has to say which
// one — but no workflow YAML names a Cove version literally either: a leg's tag is either the props
// default or a value --resolve-tags read off the registry against the floor an extension declares in
// its own manifest. The single declaration is therefore the resolver plus that declared floor, which
// is a stronger claim than a tag typed in one file, not a weaker one.
//
// No `docker`, no `curl`, no `tar`: the registry is spoken to over Node's own fetch and the layer is
// gunzipped and untarred in-process, so the script needs no binary on PATH and behaves identically
// on the Windows dev machine and the Linux runner.
//
// The layer carrying /opt/cove is selected BY CONTENT, never by index or size — the amd64 manifest
// lists that layer twice under different digests, so position and size cannot identify it. Descending
// size is only the order the candidates are probed in.
import crypto from "node:crypto";
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
const DEFAULT_CATALOG_PATH = path.join(repoRoot, "extensions", "catalog.json");

// The one member whose presence defines the layer. It is also what every consumer of the extraction
// needs, so an extraction that does not carry it is a failure rather than a smaller success.
const MARKER_MEMBER = "opt/cove/Cove.Data.dll";
const MEMBER_PREFIX = "opt/cove/";

// The assemblies the build's output-closure guard compares, and therefore the ones whose content this
// extraction has to record. Hashing every extracted file instead would go red whenever upstream
// legitimately changed an unrelated native asset under runtimes/, which is how a gate gets switched
// off; these four are the ones a NuGet copy can displace.
const GUARDED_ASSEMBLIES = ["Cove.Core.dll", "Cove.Data.dll", "Cove.Plugins.dll", "Cove.Sdk.dll"];

// Build output, imported by Directory.Build.targets, and the reason the version guard needs no
// hand-maintained constant.
const EXPECTATION_FILE = "CoveExtraction.props";

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

/**
 * Renders the MSBuild expectation the build compares its own output against.
 * <remarks>
 * The attributed `Condition="'$(X)' == ''"` form mirrors Directory.Build.props, so this file reads
 * back the way this repository's other MSBuild inputs do. Every value is checked against a strict
 * shape first: this file is IMPORTED by the build, so a registry-supplied string reaching it
 * unvalidated would be markup MSBuild evaluates rather than data it reads.
 * </remarks>
 */
export function renderExtractionProps({ tag, digest, assemblies }) {
  if (!/^[A-Za-z0-9][A-Za-z0-9_.-]*$/.test(String(tag ?? ""))) {
    throw new Error(`The extraction's image tag '${tag}' is not a plain tag name.`);
  }
  if (!/^[A-Za-z0-9][A-Za-z0-9+._-]*:[A-Fa-f0-9]+$/.test(String(digest ?? ""))) {
    throw new Error(`The resolved manifest digest '${digest}' is not an algorithm:hex digest.`);
  }
  if (assemblies.length === 0) {
    throw new Error("The extraction recorded no assemblies, so it would state no expectation.");
  }

  const items = assemblies.map(({ name, sha256 }) => {
    if (!/^[A-Za-z0-9][A-Za-z0-9.]*\.dll$/.test(name)) {
      throw new Error(`'${name}' is not a plain assembly file name.`);
    }
    if (!/^[a-f0-9]{64}$/.test(sha256)) {
      throw new Error(`'${name}' has SHA-256 '${sha256}', which is not 64 lowercase hex digits.`);
    }
    return `    <CoveExtractedAssembly Include="${name}" Sha256="${sha256}" />`;
  });

  return `<Project>
  <!--
    Generated by scripts/fetch-cove-assemblies.mjs beside the extraction it describes, and imported by
    Directory.Build.targets. Build output, never committed: the fetcher empties this directory on every
    run, so this file always describes the assemblies sitting next to it.

    The tag and digest answer "is this extraction from the image this leg is meant to test?"; the
    hashes answer "is what the suite compiled against still what the extraction wrote?". Neither
    question has a hand-maintained constant to bump.
  -->
  <PropertyGroup>
    <CoveExtractionImageTag Condition="'$(CoveExtractionImageTag)' == ''">${tag}</CoveExtractionImageTag>
    <CoveExtractionManifestDigest Condition="'$(CoveExtractionManifestDigest)' == ''">${digest}</CoveExtractionManifestDigest>
  </PropertyGroup>
  <ItemGroup>
${items.join("\n")}
  </ItemGroup>
</Project>
`;
}

// ---------------------------------------------------------------------------------------------
// Tag resolution. The ranking is pure and the paginated read takes its page reader as an argument,
// so the whole of it is driven offline by fixtures rather than by a registry round trip.
// ---------------------------------------------------------------------------------------------

// Strict X.Y.Z[-pre][+build]. This regex IS the filter: it rejects `latest`, `nightly`, the
// `sha-<hex>` digest tags and the truncated `X.Y` aliases without naming any of them, so an upstream
// tag convention nobody anticipated cannot leak in through a denylist nobody updated.
const SEMVER =
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+(?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/;

/** Parses a strict semver tag, or returns null for anything that is not one. */
export function parseSemver(tag) {
  const match = SEMVER.exec(String(tag ?? ""));
  if (match === null) return null;
  return {
    tag,
    major: Number(match[1]),
    minor: Number(match[2]),
    patch: Number(match[3]),
    prerelease: match[4] === undefined ? [] : match[4].split("."),
  };
}

/**
 * Orders two parsed versions by semver precedence, ascending.
 * <remarks>
 * Build metadata is ignored, a release outranks any pre-release of the same version, a numeric
 * identifier ranks BELOW an alphanumeric one, and a longer pre-release outranks a shorter prefix of
 * itself. Those three rules are what a naive string sort gets wrong, and getting them wrong resolves
 * a "newest" that is not the newest — a plausible answer with no error anywhere.
 * </remarks>
 */
export function compareSemver(a, b) {
  if (a.major !== b.major) return a.major - b.major;
  if (a.minor !== b.minor) return a.minor - b.minor;
  if (a.patch !== b.patch) return a.patch - b.patch;
  if (a.prerelease.length === 0 && b.prerelease.length === 0) return 0;
  if (a.prerelease.length === 0) return 1;
  if (b.prerelease.length === 0) return -1;

  for (let i = 0; i < Math.max(a.prerelease.length, b.prerelease.length); i += 1) {
    const left = a.prerelease[i];
    const right = b.prerelease[i];
    if (left === undefined) return -1;
    if (right === undefined) return 1;
    const leftNumeric = /^\d+$/.test(left);
    const rightNumeric = /^\d+$/.test(right);
    if (leftNumeric && rightNumeric) {
      if (Number(left) !== Number(right)) return Number(left) - Number(right);
    } else if (leftNumeric !== rightNumeric) {
      return leftNumeric ? -1 : 1;
    } else if (left !== right) {
      return left < right ? -1 : 1;
    }
  }
  return 0;
}

/** Splits parsed versions into GA and pre-release, each ascending so the newest is last. */
export function splitReleaseChannels(parsed) {
  const sorted = [...parsed].sort(compareSemver);
  return {
    ga: sorted.filter((version) => version.prerelease.length === 0),
    prerelease: sorted.filter((version) => version.prerelease.length > 0),
  };
}

/**
 * Resolves one extension's version legs from a registry tag list and the floor it declares.
 * <remarks>
 * Every way this can be wrong is a throw naming the value read, never a fallback: an unresolvable
 * floor that defaulted to something near it would test an image nobody chose and report green.
 * </remarks>
 */
export function resolveCoveLegs({ floor, tags, source = "the registry tag list" }) {
  const parsedFloor = parseSemver(floor);
  if (parsedFloor === null) {
    throw new Error(
      `The declared floor '${floor}' is not a strict X.Y.Z semver version, so it cannot name an image tag.`,
    );
  }

  const list = Array.isArray(tags) ? tags : [];
  if (list.length === 0) {
    throw new Error(`${source} listed no tags at all, so no version leg can be resolved.`);
  }

  const parsed = list.map(parseSemver).filter((version) => version !== null);
  if (parsed.length === 0) {
    throw new Error(
      `None of the ${list.length} tag(s) on ${source} parse as strict X.Y.Z semver, so no version leg can be resolved.`,
    );
  }

  if (!parsed.some((version) => version.tag === floor)) {
    throw new Error(
      `The declared floor '${floor}' is not published on ${source} as an exact tag (${parsed.length} semver tag(s) read). A floor leg pointing at a tag that is not there would surface as an HTTP 404 deep inside the extraction rather than here.`,
    );
  }

  const { ga, prerelease } = splitReleaseChannels(parsed);
  const newestGa = ga.at(-1);
  const newestPrerelease = prerelease.at(-1);

  const roles = [{ tag: floor, role: "floor", advisory: false }];
  if (newestGa !== undefined) {
    roles.push({ tag: newestGa.tag, role: "newest-ga", advisory: false });
  }
  // The pre-release role is advisory: an upstream release-candidate regression is not this
  // repository's defect, and a gate that can freeze merges for someone else's breakage is a gate that
  // gets switched off.
  if (newestPrerelease !== undefined) {
    roles.push({ tag: newestPrerelease.tag, role: "newest-prerelease", advisory: true });
  }

  // Dedupe by resolved tag and merge the role labels. Two roles resolving to the same tag are ONE
  // image, and a leg silently duplicating another reads as coverage while providing none — so the leg
  // count equals the distinct-image count and the merged label says what collapsed. A merged leg is
  // advisory only when every role on it is: a required role landing on a tag does not become
  // advisory because an advisory one landed there too.
  const legs = [];
  for (const candidate of roles) {
    const existing = legs.find((leg) => leg.tag === candidate.tag);
    if (existing === undefined) {
      legs.push({ ...candidate });
      continue;
    }
    existing.role = `${existing.role}+${candidate.role}`;
    existing.advisory = existing.advisory && candidate.advisory;
  }

  return {
    legs,
    examined: {
      tags: list.length,
      parsed: parsed.length,
      ga: ga.length,
      prerelease: prerelease.length,
      roles: roles.length,
    },
  };
}

/**
 * Reads every catalog entry's declared floor, reaching it through that entry's own manifest.
 * <remarks>
 * `minCoveVersion` is NOT a catalog field — it lives in the manifest the catalog's `manifestPath`
 * points at. Nothing here names an extension: a second one needs a catalog entry and no edit.
 * </remarks>
 */
export function readExtensionFloors(catalogPath = DEFAULT_CATALOG_PATH) {
  if (!fs.existsSync(catalogPath)) {
    throw new Error(`${catalogPath} does not exist, so no extension floor can be read.`);
  }
  const catalog = JSON.parse(fs.readFileSync(catalogPath, "utf8"));
  const entries = Array.isArray(catalog.extensions) ? catalog.extensions : [];
  if (entries.length === 0) {
    throw new Error(`${catalogPath} declares no extensions, so there is no floor to resolve.`);
  }

  const catalogDir = path.dirname(catalogPath);
  return entries.map((entry) => {
    const manifestPath = entry.manifestPath ?? path.posix.join(entry.path ?? "", "extension.json");
    const absolute = path.resolve(catalogDir, "..", manifestPath);
    if (!fs.existsSync(absolute)) {
      throw new Error(
        `${catalogPath} entry '${entry.id ?? entry.name}' points at manifest '${manifestPath}', which does not exist at ${absolute}.`,
      );
    }
    const manifest = JSON.parse(fs.readFileSync(absolute, "utf8"));
    const floor = manifest.minCoveVersion ?? "";
    if (floor === "") {
      throw new Error(
        `${manifestPath} declares no minCoveVersion, so the floor leg for '${entry.id ?? entry.name}' has no version to resolve against.`,
      );
    }
    return { entry, floor, manifestPath };
  });
}

/**
 * Collects a repository's whole tag list, following the registry's `Link: rel="next"` pages.
 * <remarks>
 * GHCR emits no `Link` header at today's tag count but does implement pagination, so reading one
 * page is correct today and silently truncating later — and a truncated list yields an older
 * "newest", which is a wrong answer with no error. The page cap makes a runaway an error rather than
 * a hang, and a `next` target that is not a `/v2/` path on the same host is refused rather than
 * followed: the header is registry-supplied and is not trusted to say where to go next.
 * </remarks>
 */
export async function collectRegistryTags(readPage, firstPath, pageCap = 50) {
  const collected = [];
  let pathAndQuery = firstPath;
  let pages = 0;

  while (pathAndQuery !== null) {
    const { tags, link } = await readPage(pathAndQuery);
    for (const tag of tags ?? []) collected.push(tag);
    pages += 1;

    const next = /<([^>]+)>\s*;\s*rel="next"/.exec(link ?? "");
    pathAndQuery = next === null ? null : next[1];
    if (pathAndQuery !== null && !pathAndQuery.startsWith("/v2/")) {
      throw new Error(
        `The registry's Link: rel="next" points at '${pathAndQuery}', which is not a /v2/ path on this registry; refusing to follow it.`,
      );
    }
    if (pathAndQuery !== null && pages >= pageCap) {
      throw new Error(
        `tags/list still advertised rel="next" after ${pages} page(s), at the cap of ${pageCap}; refusing to loop.`,
      );
    }
  }

  return { tags: collected, pages };
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

async function registryGetPath(registry, pathAndQuery, token, accept) {
  const url = `https://${registry}${pathAndQuery}`;
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

async function registryGet(registry, repositoryPath, token, accept) {
  return registryGetPath(registry, `/v2/${repositoryPath}`, token, accept);
}

/** Reads the repository's whole tag list off the live registry. */
async function readRegistryTags(registry, repository, token) {
  return collectRegistryTags(async (pathAndQuery) => {
    const response = await registryGetPath(registry, pathAndQuery, token);
    const body = await response.json();
    return { tags: body.tags ?? [], link: response.headers.get("link") ?? "" };
  }, `/v2/${repository}/tags/list`);
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

const USAGE =
  "Usage: fetch-cove-assemblies.mjs [--out <dir>] [--tag <semver>] | --resolve-tags [--report]";

function parseArguments(argv) {
  let out = DEFAULT_OUT_DIR;
  let tag = null;
  let mode = "extract";
  let report = false;

  for (let i = 0; i < argv.length; i += 1) {
    const argument = argv[i];
    if (argument === "--out" || argument === "--tag") {
      const value = argv[i + 1];
      if (value === undefined) throw new Error(`${argument} needs an argument.`);
      if (argument === "--out") {
        out = path.resolve(value);
      } else {
        // A tag reaches a registry URL, and --tag is how a CI leg's resolved version arrives. The
        // resolver emits strict semver only, so anything else is refused here rather than encoded
        // and sent — the floor an extension declares is validated the same way.
        if (parseSemver(value) === null) {
          throw new Error(
            `--tag '${value}' is not a strict X.Y.Z semver version. Only a resolved version tag is accepted here; the props-file default covers a moving tag.`,
          );
        }
        tag = value;
      }
      i += 1;
    } else if (argument === "--resolve-tags") {
      mode = "resolve-tags";
    } else if (argument === "--report") {
      report = true;
    } else {
      throw new Error(`Unrecognised argument '${argument}'. ${USAGE}`);
    }
  }

  if (mode === "resolve-tags" && tag !== null) {
    throw new Error("--tag names an image to extract and has no meaning for --resolve-tags.");
  }
  return { mode, out, tag, report };
}

/**
 * Resolves each catalog entry's version legs and writes them as one flat matrix on stdout.
 * <remarks>
 * stdout carries only the JSON, so a report line can never corrupt what a workflow parses; the
 * report goes to stderr, where the runner's log still shows it beside the answer it explains.
 * </remarks>
 */
async function resolveTags({ report }) {
  const image = readCoveImageReference();
  const floors = readExtensionFloors();
  const source = `${image.registry}/${image.repository}`;

  const token = await fetchPullToken(image.registry, image.repository);
  const { tags, pages } = await readRegistryTags(image.registry, image.repository, token);

  const lines = [`tags/list on ${source} returned ${tags.length} tag(s) over ${pages} page(s)`];
  const include = [];

  for (const { entry, floor, manifestPath } of floors) {
    const resolved = resolveCoveLegs({ floor, tags, source });
    lines.push(
      `${entry.name}: floor ${floor} (from ${manifestPath}); of ${resolved.examined.tags} tag(s) ${resolved.examined.parsed} parse as strict semver (${resolved.examined.ga} GA, ${resolved.examined.prerelease} pre-release)`,
    );
    for (const leg of resolved.legs) {
      const merged = leg.role.split("+");
      lines.push(
        `  leg ${leg.role}: ${leg.tag}${leg.advisory ? " (advisory)" : ""}${
          merged.length > 1 ? ` — ${merged.length} roles resolved to this one image` : ""
        }`,
      );
      include.push({ extension: entry, cove: leg });
    }
    // The load-bearing figure. A resolver whose log prints tags but not a distinct count is exactly
    // the shape that reads as N version legs while testing fewer images than that.
    lines.push(
      `  ${resolved.legs.length} distinct image(s) from ${resolved.examined.roles} role(s) for ${entry.name}`,
    );
  }

  process.stdout.write(`${JSON.stringify({ include })}\n`);
  if (report) {
    for (const line of lines) console.error(line);
  }
  return 0;
}

export async function main(argv) {
  const options = parseArguments(argv);
  if (options.mode === "resolve-tags") return resolveTags(options);
  return extract(options);
}

async function extract({ out, tag }) {
  const image = readCoveImageReference();
  const requestedTag = tag ?? image.tag;

  console.log(`Cove test image: ${image.registry}/${image.repository}:${requestedTag}`);

  const token = await fetchPullToken(image.registry, image.repository);
  const { digest, manifest } = await resolvePlatformManifest(
    image.registry,
    image.repository,
    requestedTag,
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

  const assemblies = GUARDED_ASSEMBLIES.map((name) => {
    const file = path.join(out, name);
    if (!fs.existsSync(file)) {
      throw new Error(
        `The extraction left no ${name} in ${out}, so the build's output-closure guard would have nothing to compare that assembly against.`,
      );
    }
    return {
      name,
      sha256: crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex"),
    };
  });
  fs.writeFileSync(
    path.join(out, EXPECTATION_FILE),
    renderExtractionProps({ tag: requestedTag, digest, assemblies }),
  );
  console.log(`recorded expectation: ${EXPECTATION_FILE} for tag ${requestedTag}`);

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

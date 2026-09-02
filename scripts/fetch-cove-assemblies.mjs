// Extracts Cove's published assemblies from the released `cove-app` image, so a CI leg can build and
// test against the binaries the released host actually loads. A source build is not what anyone runs,
// and there is no NuGet closure to use instead: Cove.Data is on no feed.
//
// The image repository comes from Directory.Build.props and is never a literal here, so a rename
// fails this script's own tests rather than drifting. A tag may arrive via --tag.
//
// Extraction is `docker pull` + `docker create` + `docker cp`, which leaves layer selection and
// digest verification to the daemon. Both moby's classic path and containerd's verify every layer,
// but the OCI distribution spec only says a client SHOULD, so re-check that if this is ever moved
// onto a different puller. It also means assemblies mode needs a Docker that can run LINUX
// containers; macOS and Windows runners are therefore permanently bare (`CoveSourceMode=none`).
//
// `--resolve-tags` stays registry HTTP because there is no `docker` verb for listing remote tags.
import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { execFileSync } from "node:child_process";

// import.meta.dirname, never a filesystem path read off a module URL's path component: on Windows
// that yields a leading-slash form which resolves to a doubled drive prefix.
const repoRoot = path.resolve(import.meta.dirname, "..");

const DEFAULT_PROPS_PATH = path.join(repoRoot, "Directory.Build.props");
const DEFAULT_OUT_DIR = path.join(repoRoot, "artifacts", "cove-assemblies");
const DEFAULT_CATALOG_PATH = path.join(repoRoot, "extensions", "catalog.json");

// The one member whose presence defines the layer, so an extraction without it is a failure.
const MARKER_MEMBER = "opt/cove/Cove.Data.dll";
const MEMBER_PREFIX = "opt/cove/";

// Derived from MEMBER_PREFIX rather than written again, so the path copied from and the member that
// proves the copy worked cannot drift apart. The trailing `/.` is what makes docker copy the
// directory's CONTENTS rather than the directory.
const CONTAINER_SOURCE = `/${MEMBER_PREFIX}.`;

// The assemblies the build's output-closure guard compares. Hashing every extracted file instead
// would go red on any unrelated native asset upstream changed under runtimes/; these four are the
// ones a NuGet copy can displace.
const GUARDED_ASSEMBLIES = ["Cove.Core.dll", "Cove.Data.dll", "Cove.Plugins.dll", "Cove.Sdk.dll"];

// Build output, imported by Directory.Build.targets, and the reason the version guard needs no
// hand-maintained constant.
const EXPECTATION_FILE = "CoveExtraction.props";

// A CONTRACT, not diagnostics: build.yml reads these three lines back with line-start-anchored
// `grep -E`, and a rename or a re-flow silently empties the shell variable that reads it. Pinned
// against the workflow's own greps in the test file so drift on either side goes red.
export const STDOUT_CONTRACT = Object.freeze({
  digest: "manifest digest: ",
  assemblyVersion: "Cove.Data.dll assembly version: ",
  informationalVersion: "Cove.Data.dll informational version: ",
});

// ---- Pure helpers: no network, no disk, so the tests drive them with fixtures. ----

/**
 * Reads the flat `<Name>value</Name>` property elements out of an MSBuild file's text.
 * Mirrors scripts/validate-extension-repo.mjs's reader rather than introducing a second dialect:
 * later declarations win, and a `$(Other)` reference expands from what has already been read.
 */
export function parseMsBuildProperties(content) {
  const props = {};
  const pattern = /<([A-Za-z_][A-Za-z0-9_.-]*)(?:\s[^>]*)?>([^<]*)<\/\1>/g;
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
 *
 * The registry host is taken from the reference itself and never from an argument, so the token
 * endpoint and the blob endpoint are always the same host the declared image names. A reference
 * with no host component is rejected rather than defaulted to Docker Hub: this repo declares one
 * image, and guessing a different registry for a malformed value is how a fetch ends up somewhere
 * nobody named.
 */
export function splitImageReference(reference) {
  const value = String(reference ?? "").trim();
  if (value === "") throw new Error("The Cove test image repository is empty.");
  if (/^[A-Za-z][A-Za-z0-9+.-]*:\/\//.test(value)) {
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
 * Picks the `algorithm:hex` digest `docker image inspect` recorded for one specific repository.
 *
 * `RepoDigests` carries one entry per repository the image is known under, so index 0 can name a
 * DIFFERENT repository. Matched on the repository instead, and two digests for the same repository
 * are refused rather than resolved by position. An image built locally and never pulled has no
 * registry digest at all, hence the throw rather than an empty value.
 */
export function selectRepoDigest(repoDigests, repository) {
  const entries = (repoDigests ?? []).filter(
    (entry) => typeof entry === "string" && entry.trim() !== "",
  );
  if (entries.length === 0) {
    throw new Error(
      `docker recorded no RepoDigests for ${repository}, so this extraction has no registry digest to attribute itself to. An image built locally and never pulled has none.`,
    );
  }

  const prefix = `${repository}@`;
  const matching = [...new Set(entries.filter((entry) => entry.startsWith(prefix)))];
  if (matching.length === 0) {
    throw new Error(
      `None of docker's ${entries.length} RepoDigests entr(y|ies) name ${repository}: ${entries.join(", ")}.`,
    );
  }
  if (matching.length > 1) {
    throw new Error(
      `docker recorded ${matching.length} different digests for ${repository}: ${matching.join(", ")}. Which one is current is ambiguous, so refusing to record either.`,
    );
  }

  const digest = matching[0].slice(prefix.length);
  if (!/^[A-Za-z0-9][A-Za-z0-9+._-]*:[A-Fa-f0-9]+$/.test(digest)) {
    throw new Error(
      `docker recorded '${digest}' for ${repository}, which is not an algorithm:hex digest.`,
    );
  }
  return digest;
}

/**
 * Reads the Win32 version-resource strings a .NET assembly carries — `Assembly Version` is the
 * managed assembly version and `ProductVersion` the informational one.
 *
 * A diagnostic reader, not the gate: the build's own `GetAssemblyIdentity` task is what asserts the
 * version. Keys and values in the resource are UTF-16LE and 4-byte aligned, so the value is found by
 * stepping over the padding zeros after the key's terminator. A key that is absent yields no entry
 * rather than a guess.
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
 *
 * The attributed `Condition="'$(X)' == ''"` form mirrors Directory.Build.props, so this file reads
 * back the way this repository's other MSBuild inputs do. Every value is checked against a strict
 * shape first: this file is IMPORTED by the build, so a registry-supplied string reaching it
 * unvalidated would be markup MSBuild evaluates rather than data it reads.
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

// ---- Tag resolution: ranking is pure and the paginated read takes its page reader as an argument. ----

// Strict X.Y.Z[-pre][+build]. This regex IS the filter: it rejects `latest`, `nightly`, the
// `sha-<hex>` digest tags and the truncated `X.Y` aliases without naming any of them, so an upstream
// tag convention nobody anticipated cannot leak in through a denylist nobody updated.
const SEMVER =
  /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+(?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$/;

/** Parses a strict semver tag, or returns null for anything that is not one. */
/**
 * True when the tag on `image` is at or above `floor`.
 *
 * Two tag shapes are not plain versions. A non-semver tag (`nightly`, `latest`) counts as at or
 * above, since those track ahead of the last release. A prerelease (`1.2.0-rc.1`) sorts BELOW its own
 * release per semver, so it reads as lacking the capability even when it carries it — a skip rather
 * than a failure.
 *
 * @param {string} image - a complete image reference, e.g. `ghcr.io/yourcove/cove-app:1.3.0`.
 * @param {string} floor - the release the capability arrived in, as strict X.Y.Z.
 * @returns {boolean}
 */
export function imageAtLeastVersion(image, floor) {
  const target = parseSemver(floor);
  if (target === null)
    throw new Error(`imageAtLeastVersion needs a strict X.Y.Z floor, got '${floor}'.`);
  const parsed = parseSemver(image.slice(image.lastIndexOf(":") + 1));
  return parsed === null || compareSemver(parsed, target) >= 0;
}

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
 *
 * Build metadata is ignored, a release outranks any pre-release of the same version, a numeric
 * identifier ranks BELOW an alphanumeric one, and a longer pre-release outranks a shorter prefix of
 * itself. Those are the rules a naive string sort gets wrong.
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
 *
 * Every failure is a throw naming the value read, never a fallback: a defaulted floor would test an
 * image nobody chose and report green.
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
  // The newest GA AT OR ABOVE the floor, never the newest published: a required leg below the declared
  // floor boots a host that declines to load the extension, so every route 404s and every browser spec
  // fails with nothing anywhere naming a version. While the floor is itself the newest GA the two
  // collapse onto one image and that failure cannot be seen at all. When no GA reaches the floor the
  // role is omitted rather than pointed at the nearest thing to it — the same refusal to substitute a
  // plausible answer as the throws above.
  const newestGa = ga.findLast((version) => compareSemver(version, parsedFloor) >= 0);
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
 * Reads each catalog entry's declared floor, reaching it through that entry's own manifest.
 *
 * `minCoveVersion` is NOT a catalog field — it lives in the manifest the catalog's `manifestPath`
 * points at. Nothing here names an extension: a second one needs a catalog entry and no edit.
 *
 * `select` narrows which entries are read AT ALL, not which results come back: a manifest that is
 * absent or declares no floor throws, so an entry a caller does not care about could otherwise fail
 * that caller. Omitted, every entry is read, which is what the CI version matrix wants.
 *
 * @param {(entry: object) => boolean} [select]
 * @param {string} [catalogPath]
 */
/**
 * Picks the highest floor from what `readExtensionFloors` returned, by semver precedence.
 *
 * Throws on an empty list rather than returning a default, because the caller's next act is to check
 * out a ref: a silent fallback there formats and analyses against a version nothing declared.
 * A floor that does not parse is a hard failure for the same reason.
 */
export function highestDeclaredFloor(declared) {
  if (!Array.isArray(declared) || declared.length === 0) {
    throw new Error("No extension floor was declared, so there is no Cove ref to resolve.");
  }
  // Parsed up front rather than inside the reduce: a single-entry list never invokes the callback, so
  // a lone unparseable floor would otherwise be returned unchecked.
  const parsed = declared.map((candidate) => {
    const version = parseSemver(candidate.floor);
    if (version === null) {
      throw new Error(
        `${candidate.entry.name} declares a floor that is not a semver: ${candidate.floor}`,
      );
    }
    return { candidate, version };
  });
  return parsed.reduce(
    (left, right) => (compareSemver(left.version, right.version) >= 0 ? left : right),
    parsed[0],
  ).candidate;
}

export function readExtensionFloors(select, catalogPath = DEFAULT_CATALOG_PATH) {
  if (!fs.existsSync(catalogPath)) {
    throw new Error(`${catalogPath} does not exist, so no extension floor can be read.`);
  }
  const catalog = JSON.parse(fs.readFileSync(catalogPath, "utf8"));
  const entries = Array.isArray(catalog.extensions) ? catalog.extensions : [];
  if (entries.length === 0) {
    throw new Error(`${catalogPath} declares no extensions, so there is no floor to resolve.`);
  }

  const catalogDir = path.dirname(catalogPath);
  const selected = select ? entries.filter(select) : entries;
  return selected.map((entry) => {
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
 *
 * GHCR emits no `Link` header at today's tag count but does implement pagination, so reading one
 * page is correct today and silently truncating later — and a truncated list yields an older
 * "newest", which is a wrong answer with no error. The page cap makes a runaway an error rather than
 * a hang, and a `next` target that is not a `/v2/` path on the same host is refused rather than
 * followed: the header is registry-supplied and is not trusted to say where to go next.
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
  const scope = `repository:${repository}:pull`;
  const url = `https://${registry}/token?service=${encodeURIComponent(registry)}&scope=${encodeURIComponent(scope)}`;
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

/** Reads the repository's whole tag list off the live registry. */
async function readRegistryTags(registry, repository, token) {
  return collectRegistryTags(async (pathAndQuery) => {
    const response = await registryGetPath(registry, pathAndQuery, token);
    const body = await response.json();
    return { tags: body.tags ?? [], link: response.headers.get("link") ?? "" };
  }, `/v2/${repository}/tags/list`);
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
        // The extraction empties this directory recursively before it writes. A path that CONTAINS the
        // repository therefore deletes the working tree, and `--out .` from the repo root is one
        // keystroke away from `--out ./artifacts`. CI always passes a fixed path, so this refuses the
        // developer typo rather than a live defect. Compared case-insensitively on Windows because a
        // drive letter arrives in either case there and a case-sensitive prefix test would miss.
        const same = (a, b) =>
          process.platform === "win32" ? a.toLowerCase() === b.toLowerCase() : a === b;
        // A drive root ("I:\") and a POSIX root ("/") already END in the separator, so appending one
        // yields a doubled prefix that matches nothing — and the root is the most destructive target
        // there is. Normalise to exactly one trailing separator before comparing.
        const asPrefix = (dir) => (dir.endsWith(path.sep) ? dir : dir + path.sep);
        const contains = (ancestor, descendant) =>
          same(descendant.slice(0, asPrefix(ancestor).length), asPrefix(ancestor));
        if (same(out, repoRoot) || contains(out, repoRoot)) {
          throw new Error(
            `--out '${out}' is the repository or contains it, and the extraction empties its target recursively. Refusing to delete the working tree.`,
          );
        }
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
 *
 * stdout carries only the JSON, so a report line can never corrupt what a workflow parses; the
 * report goes to stderr, where the runner's log still shows it beside the answer it explains.
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

/**
 * Renders the two `Cove.Data.dll` version lines the workflow greps, in the workflow's own spelling.
 *
 * An unreadable key prints `unreadable` rather than an empty value, so the line keeps its anchored
 * prefix and the notice says the version could not be read instead of silently reading as blank.
 */
export function renderVersionLines(versions) {
  return [
    `${STDOUT_CONTRACT.assemblyVersion}${versions["Assembly Version"] ?? "unreadable"}`,
    `${STDOUT_CONTRACT.informationalVersion}${versions.ProductVersion ?? "unreadable"}`,
  ];
}

/**
 * Refuses an extraction that wrote nothing or left no marker member, returning the marker's path.
 *
 * The failure this exists for is a wrong container source path, which produces an EMPTY output
 * directory rather than a wrong one — and an empty extraction that returned quietly would surface as
 * a smaller green test run instead of a failure. fs-only and separated from the copy so both arms are
 * provable without Docker.
 */
export function assertExtractionNotEmpty(out, written) {
  const marker = path.posix.basename(MARKER_MEMBER);
  const markerPath = path.join(out, marker);
  if (written === 0 || !fs.existsSync(markerPath)) {
    throw new Error(
      `The extraction wrote ${written} file(s) and left no ${marker} in ${out}. An empty extraction fails here rather than later as a quietly smaller test run.`,
    );
  }
  return markerPath;
}

/**
 * Hashes each guarded assembly, refusing if any is absent from the extraction.
 *
 * One missing assembly means the build's output-closure guard would have nothing to compare that
 * assembly against — a gate that silently covers three of four rather than a gate that fails.
 */
export function readGuardedAssemblies(out) {
  return GUARDED_ASSEMBLIES.map((name) => {
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
}

/**
 * Runs one `docker` invocation and returns its stdout, or throws naming what failed.
 *
 * `execFileSync` with no shell, so an argument is an argument: a registry-supplied tag reaches the
 * command as one argv element and is never text a shell parses. docker's own stderr is folded into
 * the thrown message, because "docker exited 1" without it says nothing about why.
 *
 * The binary is named, not resolved to an absolute path, and that is a decision rather than an
 * oversight. There is no portable absolute path to resolve to: Docker Engine, Docker Desktop,
 * Colima and Rancher each place the binary somewhere different across the three operating systems
 * this repo builds on, so hardcoding one would break the script everywhere it does not match. The
 * exposure that buys is PATH substitution, which requires an attacker who can already write to a
 * directory on PATH — on a throwaway CI runner or the maintainer's own machine, someone with that
 * access does not need this script. Revisit if this ever runs somewhere PATH is not trusted.
 */
// Hoisted so the call below fits on one line: the suppression has to sit on the line the issue is
// reported at, and Prettier relocates a trailing comment that follows an inline object's `{`.
const DOCKER_STDIO = { encoding: "utf8", stdio: ["ignore", "pipe", "pipe"] };

function runDocker(args) {
  try {
    return execFileSync("docker", args, DOCKER_STDIO).trim(); // NOSONAR javascript:S4036
  } catch (error) {
    const detail = String(error.stderr ?? "").trim() || error.message;
    throw new Error(`docker ${args.join(" ")} failed: ${detail}`, { cause: error });
  }
}

/** Counts the regular files under a directory tree — the extraction's own size, reported for evidence. */
function countFiles(directory) {
  let total = 0;
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const full = path.join(directory, entry.name);
    if (entry.isDirectory()) total += countFiles(full);
    else total += 1;
  }
  return total;
}

async function extract({ out, tag }) {
  const image = readCoveImageReference();
  const requestedTag = tag ?? image.tag;
  const repository = `${image.registry}/${image.repository}`;
  const reference = `${repository}:${requestedTag}`;

  console.log(`Cove test image: ${reference}`);

  // Pulled explicitly rather than left to `docker create`'s implicit pull. `create` is satisfied by
  // whatever image already carries the tag locally, so on a machine holding a stale copy it would
  // extract bytes the registry no longer serves — silently, and the registry-protocol reader this
  // replaces always read the registry. A no-op "Image is up to date" is the cost of keeping that.
  runDocker(["pull", reference]);

  // The manifest-list (image index) digest, NOT the platform-manifest digest the registry-protocol
  // reader used to record: `RepoDigests` is what the daemon stored for the reference it pulled, and
  // for a multi-platform tag that is the index. Both identify the same pull and neither is
  // hand-maintained, and Directory.Build.targets compares the TAG rather than this — the digest rides
  // as provenance. So the value here is self-consistent but is NOT byte-comparable with a digest
  // captured before this rewrite; a diff of exactly this line between the two is expected.
  const digest = selectRepoDigest(
    JSON.parse(runDocker(["image", "inspect", reference, "--format", "{{json .RepoDigests}}"])),
    repository,
  );
  console.log(`${STDOUT_CONTRACT.digest}${digest}`);

  // Emptied, not merged into: CoveExtraction.props states what sits beside it, so a file left over
  // from a previous run under a different tag would be described by a props file that never saw it.
  fs.rmSync(out, { recursive: true, force: true });
  fs.mkdirSync(out, { recursive: true });

  // `docker create` starts nothing, so no entrypoint runs and no database is touched — the container
  // exists only to give `docker cp` a filesystem to read. Removed in a `finally`: an extraction that
  // throws between here and there would otherwise leak one container per run.
  const containerId = runDocker(["create", reference]);
  try {
    runDocker(["cp", `${containerId}:${CONTAINER_SOURCE}`, `${out}${path.sep}`]);
  } finally {
    runDocker(["rm", "--force", containerId]);
  }

  const written = countFiles(out);
  const markerPath = assertExtractionNotEmpty(out, written);

  console.log(`files written: ${written}`);
  for (const line of renderVersionLines(readVersionStrings(fs.readFileSync(markerPath)))) {
    console.log(line);
  }

  const assemblies = readGuardedAssemblies(out);
  fs.writeFileSync(
    path.join(out, EXPECTATION_FILE),
    renderExtractionProps({ tag: requestedTag, digest, assemblies }),
  );
  console.log(`recorded expectation: ${EXPECTATION_FILE} for tag ${requestedTag}`);

  console.log(`output directory: ${out}`);
  return 0;
}

/**
 * Whether this process was STARTED from this file, rather than importing it for its helpers.
 *
 * Both sides are realpathed rather than compared as resolved strings: Node realpaths the module URL
 * and leaves process.argv[1] as the caller spelled it, so an invocation through a junction or symlink
 * — the shape this repo's worktree workflow uses — compares unequal and the refusal below never
 * fires. Windows drive-letter casing is normalised for the same reason.
 */
function invokedAsScript() {
  const entry = process.argv[1];
  if (typeof entry !== "string" || entry === "") return false;
  const canonical = (value) => {
    let resolved = path.resolve(value);
    try {
      resolved = fs.realpathSync.native(resolved);
    } catch {
      // Left as resolved: a path that cannot be realpathed is one that does not exist, and comparing
      // the resolved form is no weaker than not comparing at all.
    }
    return process.platform === "win32" ? resolved.toLowerCase() : resolved;
  };
  return canonical(entry) === canonical(import.meta.filename);
}

// `import.meta.main` is a boolean from Node 22.18 onward and `undefined` before it, so a bare
// `if (import.meta.main)` takes the not-main branch on an older runtime: run as a CLI, this script
// then prints nothing and exits 0. That is measured, not theorised — on v22.6.0, a version volta has
// installed, it produced zero bytes and exit 0. A script that does nothing and reports success is
// worse than one that crashes, so the absent feature is refused BY NAME instead of being tolerated.
//
// The root package.json declares `engines.node: ">=22.18"`, which is the version this property became
// a boolean, but that declaration is advice and not a gate: without engine-strict, npm prints
// EBADENGINE and installs anyway, and a script run directly never consults it at all. The refusal is
// therefore the enforcement, and deleting it as redundant would restore the silent no-op on a runtime
// the declaration only asks contributors to avoid.
//
// Scoped to the CLI on purpose: the pure helpers this module exports work fine on an older Node, and
// refusing at import time would break the e2e harness and this file's own tests for a feature only the
// entry guard needs.
if (typeof import.meta.main !== "boolean") {
  if (invokedAsScript()) {
    console.error(
      `fetch-cove-assemblies: this Node (${process.version}) does not implement import.meta.main, so this script cannot tell it was run rather than imported and would print nothing while exiting 0. Node 22.18 or newer is required to run it.`,
    );
    process.exitCode = 1;
  }
} else if (import.meta.main) {
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

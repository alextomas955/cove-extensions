// Resolves which Cove versions each extension is tested against, from the floor its manifest declares
// and the tags the released `cove-app` image publishes. CI fans the result out as its version matrix
// and checks Cove's source out at the highest declared floor; the e2e harness imports the floor and
// semver helpers.
//
// The image repository comes from Directory.Build.props and is never a literal here.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { parseMsBuildProperties, readJson } from "./repo-files.mjs";

const repoRoot = path.resolve(import.meta.dirname, "..");

const DEFAULT_PROPS_PATH = path.join(repoRoot, "Directory.Build.props");
const DEFAULT_CATALOG_PATH = path.join(repoRoot, "extensions", "catalog.json");

/**
 * Splits `ghcr.io/yourcove/cove-app` into its registry host and repository path.
 *
 * The registry host is taken from the reference itself and never from an argument, so the token
 * endpoint and the tag endpoint are always the same host the declared image names. A reference
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

// ---- Tag resolution: ranking is pure and the paginated read takes its page reader as an argument. ----

// The identifier grammar of strict X.Y.Z[-pre][+build], one rule per identifier kind. The parser
// below is the filter: it rejects `latest`, `nightly`, the `sha-<hex>` digest tags and the truncated
// `X.Y` aliases without naming any of them, so an upstream tag convention nobody anticipated cannot
// leak in through a denylist nobody updated.
const CORE_NUMBER = /^(?:0|[1-9]\d*)$/;
const PRERELEASE_IDENTIFIER = /^(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)$/;
const BUILD_IDENTIFIER = /^[0-9A-Za-z-]+$/;

/**
 * True when the tag on `image` is at or above `floor`.
 *
 * Two tag shapes are not plain versions. A non-semver tag (`nightly`, `latest`) counts as at or
 * above, since those track ahead of the last release. A prerelease (`1.2.0-rc.1`) sorts below its own
 * release per semver, so it reads as lacking the capability even when it carries it - a skip rather
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

/** Parses a strict semver tag, or returns null for anything that is not one. */
export function parseSemver(tag) {
  const text = String(tag ?? "");
  const plus = text.indexOf("+");
  const withoutBuild = plus === -1 ? text : text.slice(0, plus);
  const build = plus === -1 ? [] : text.slice(plus + 1).split(".");
  if (!build.every((id) => BUILD_IDENTIFIER.test(id))) return null;

  // The first hyphen ends the core: a pre-release identifier may itself contain hyphens.
  const hyphen = withoutBuild.indexOf("-");
  const core = hyphen === -1 ? withoutBuild : withoutBuild.slice(0, hyphen);
  const prerelease = hyphen === -1 ? [] : withoutBuild.slice(hyphen + 1).split(".");
  const numbers = core.split(".");
  if (numbers.length !== 3 || !numbers.every((part) => CORE_NUMBER.test(part))) return null;
  if (!prerelease.every((id) => PRERELEASE_IDENTIFIER.test(id))) return null;

  const [major, minor, patch] = numbers.map(Number);
  return { tag, major, minor, patch, prerelease };
}

/**
 * Orders two parsed versions by semver precedence, ascending.
 *
 * Build metadata is ignored, a release outranks any pre-release of the same version, a numeric
 * identifier ranks below an alphanumeric one, and a longer pre-release outranks a shorter prefix of
 * itself. Those are the rules a naive string sort gets wrong.
 */
export function compareSemver(a, b) {
  if (a.major !== b.major) return a.major - b.major;
  if (a.minor !== b.minor) return a.minor - b.minor;
  if (a.patch !== b.patch) return a.patch - b.patch;
  return comparePrerelease(a.prerelease, b.prerelease);
}

function comparePrerelease(left, right) {
  if (left.length === 0 || right.length === 0) return right.length - left.length;
  for (let i = 0; i < Math.min(left.length, right.length); i += 1) {
    const order = comparePrereleaseIdentifier(left[i], right[i]);
    if (order !== 0) return order;
  }
  return left.length - right.length;
}

function comparePrereleaseIdentifier(left, right) {
  const leftNumeric = /^\d+$/.test(left);
  const rightNumeric = /^\d+$/.test(right);
  if (leftNumeric && rightNumeric) return Number(left) - Number(right);
  if (leftNumeric !== rightNumeric) return leftNumeric ? -1 : 1;
  if (left === right) return 0;
  return left < right ? -1 : 1;
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
      `The declared floor '${floor}' is not published on ${source} as an exact tag (${parsed.length} semver tag(s) read). A floor leg pointing at a tag that is not there would fail later, as an image pull or a checkout with no such version.`,
    );
  }

  const { ga, prerelease } = splitReleaseChannels(parsed);
  // The newest GA at or above the floor, never the newest published: a required leg below the declared
  // floor boots a host that declines to load the extension, so every route 404s and every browser spec
  // fails with nothing anywhere naming a version. While the floor is itself the newest GA the two
  // collapse onto one image and that failure cannot be seen at all. When no GA reaches the floor the
  // role is omitted rather than pointed at the nearest thing to it - the same refusal to substitute a
  // plausible answer as the throws above.
  const newestGa = ga.findLast((version) => compareSemver(version, parsedFloor) >= 0);
  // A pre-release that sorts below the newest GA is a build that release superseded, and it may sit
  // below the floor too. Upstream numbers a dev build after the release before it, so the newest
  // pre-release published can be one of these.
  const candidate = prerelease.at(-1);
  const newestPrerelease =
    candidate !== undefined && (newestGa === undefined || compareSemver(candidate, newestGa) > 0)
      ? candidate
      : undefined;

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

  // Dedupe by resolved tag and merge the role labels. Two roles resolving to the same tag are one
  // image, and a leg silently duplicating another reads as coverage while providing none - so the leg
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

/**
 * Reads each catalog entry's declared floor, reaching it through that entry's own manifest.
 *
 * `minCoveVersion` is not a catalog field - it lives in the manifest the catalog's `manifestPath`
 * points at. Nothing here names an extension: a second one needs a catalog entry and no edit.
 *
 * `select` narrows which entries are read at all, not which results come back: a manifest that is
 * absent or declares no floor throws, so an entry a caller does not care about could otherwise fail
 * that caller. Omitted, every entry is read, which is what the CI version matrix wants.
 *
 * @param {(entry: object) => boolean} [select]
 * @param {string} [catalogPath]
 */
export function readExtensionFloors(select, catalogPath = DEFAULT_CATALOG_PATH) {
  if (!fs.existsSync(catalogPath)) {
    throw new Error(`${catalogPath} does not exist, so no extension floor can be read.`);
  }
  const catalog = readJson(catalogPath);
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
    const manifest = readJson(absolute);
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
 * The target of the `rel="next"` entry in a Link header, or null when it advertises none.
 *
 * Parsed by splitting rather than by one pattern, so the cost stays linear in the header's length.
 */
function nextLinkTarget(link) {
  for (const entry of String(link ?? "").split(",")) {
    const [target, ...params] = entry.split(";").map((part) => part.trim());
    if (params.includes('rel="next"') && target.startsWith("<") && target.endsWith(">")) {
      return target.slice(1, -1);
    }
  }
  return null;
}

/**
 * Collects a repository's whole tag list, following the registry's `Link: rel="next"` pages.
 *
 * GHCR emits no `Link` header at today's tag count but does implement pagination, so reading one
 * page is correct today and silently truncating later - and a truncated list yields an older
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

    pathAndQuery = nextLinkTarget(link);
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

// ---- Registry reads. ----

/** Reads the declared Cove test image repository out of Directory.Build.props. */
export function readCoveImageReference(propsPath = DEFAULT_PROPS_PATH) {
  if (!fs.existsSync(propsPath)) {
    throw new Error(
      `${propsPath} does not exist, so the Cove test image reference cannot be read.`,
    );
  }
  const props = parseMsBuildProperties(fs.readFileSync(propsPath, "utf8"));
  const repository = props.CoveTestImageRepository ?? "";
  if (repository === "") {
    throw new Error(`${propsPath} must declare CoveTestImageRepository.`);
  }
  return splitImageReference(repository);
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
  const headers = { authorization: `Bearer ${token}`, ...(accept ? { accept } : {}) };
  const response = await fetch(url, { headers });
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

const MANIFEST_ACCEPT = [
  "application/vnd.oci.image.index.v1+json",
  "application/vnd.docker.distribution.manifest.list.v2+json",
  "application/vnd.oci.image.manifest.v1+json",
  "application/vnd.docker.distribution.manifest.v2+json",
].join(", ");

const REVISION_LABEL = "org.opencontainers.image.revision";
const DIGEST = /^sha256:[0-9a-f]{64}$/;

// A digest arrives in a registry response and goes into the next request's path, so it is checked and
// encoded first. Error messages leave the value out because they reach the log.
function digestSegment(digest, what) {
  if (typeof digest !== "string" || !DIGEST.test(digest)) {
    throw new TypeError(`${what} is not a sha256 digest.`);
  }
  return encodeURIComponent(digest);
}

/**
 * The Cove commit an image tag was built from, read from the image's OCI revision label.
 *
 * A dev pre-release is an image tag with no git tag of the same name, so `v<tag>` names nothing to
 * check out. `readJson` takes a registry path and returns the parsed body. Throws when a digest or the
 * label is not in its expected form.
 */
export async function readImageRevision(readJson, repository, tag) {
  const image = `${repository}:${tag}`;
  let manifest = await readJson(`/v2/${repository}/manifests/${encodeURIComponent(tag)}`);
  if (Array.isArray(manifest.manifests)) {
    const entry = manifest.manifests.find(
      (m) => m.platform?.os === "linux" && m.platform?.architecture === "amd64",
    );
    if (entry === undefined) {
      throw new Error(`${image} is an image index with no linux/amd64 image.`);
    }
    const digest = digestSegment(entry.digest, `The linux/amd64 entry in ${image}'s index`);
    manifest = await readJson(`/v2/${repository}/manifests/${digest}`);
  }
  const configDigest = digestSegment(manifest.config?.digest, `The config digest of ${image}`);
  const config = await readJson(`/v2/${repository}/blobs/${configDigest}`);
  const revision = config.config?.Labels?.[REVISION_LABEL];
  if (typeof revision !== "string" || !/^[0-9a-f]{40}$/.test(revision)) {
    throw new Error(
      `${image} carries no ${REVISION_LABEL} label holding a full commit hash, so its source cannot be checked out.`,
    );
  }
  return revision;
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

/**
 * The version matrix built from the declared floors alone, one floor leg per extension, with no
 * registry read. A pull request uses it so a re-run of the same commit examines the same hosts.
 */
function floorLegs() {
  const include = [];
  for (const { entry, floor, manifestPath } of readExtensionFloors()) {
    console.error(`floor leg: ${entry.name} ${floor} (from ${manifestPath})`);
    include.push({ extension: entry, cove: { tag: floor, role: "floor", advisory: false } });
  }
  process.stdout.write(`${JSON.stringify({ include })}\n`);
  return 0;
}

/**
 * The Cove git ref a job checks out: the highest floor any extension declares, printed as a
 * `ref=v<floor>` line for $GITHUB_OUTPUT. Every floor it saw goes to stderr.
 */
function coveRef() {
  const declared = readExtensionFloors();
  const highest = highestDeclaredFloor(declared);
  for (const { entry, floor, manifestPath } of declared) {
    console.error(`${entry.name} declares floor ${floor} (from ${manifestPath})`);
  }
  const distinct = new Set(declared.map((d) => d.floor));
  if (distinct.size > 1) {
    console.error(
      `::notice::${distinct.size} distinct floors are declared; using the highest, ${highest.floor}.`,
    );
  }
  process.stdout.write(`ref=v${highest.floor}\n`);
  return 0;
}

/**
 * The Cove git ref holding one version's source, as a `ref=` line for $GITHUB_OUTPUT. A release is its
 * `v<version>` tag. A pre-release is the commit its image was built from.
 */
async function sourceRef(version) {
  const parsed = parseSemver(version);
  if (parsed === null) {
    throw new Error(`'${version}' is not a semver version, so it names no Cove source.`);
  }
  if (parsed.prerelease.length === 0) {
    process.stdout.write(`ref=v${version}\n`);
    return 0;
  }
  const image = readCoveImageReference();
  const token = await fetchPullToken(image.registry, image.repository);
  const revision = await readImageRevision(
    async (pathAndQuery) =>
      (await registryGetPath(image.registry, pathAndQuery, token, MANIFEST_ACCEPT)).json(),
    image.repository,
    version,
  );
  process.stdout.write(`ref=${revision}\n`);
  return 0;
}

const USAGE =
  "Usage: cove-versions.mjs [--report] | --floors-only | --cove-ref | --source-ref <version>";

export async function main(argv) {
  if (argv.length === 1 && argv[0] === "--floors-only") return floorLegs();
  if (argv.length === 1 && argv[0] === "--cove-ref") return coveRef();
  if (argv.length === 2 && argv[0] === "--source-ref") return sourceRef(argv[1]);
  const unknown = argv.filter((argument) => argument !== "--report");
  if (unknown.length > 0) {
    throw new Error(`Unrecognised argument '${unknown[0]}'. ${USAGE}`);
  }
  return resolveTags({ report: argv.includes("--report") });
}

if (import.meta.main) {
  try {
    process.exitCode = await main(process.argv.slice(2));
  } catch (error) {
    console.error(`cove-versions: ${error.message}`);
    process.exitCode = 1;
  }
}

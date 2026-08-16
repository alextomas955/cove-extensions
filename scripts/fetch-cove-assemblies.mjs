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
// The extraction is `docker pull` + `docker create` + `docker cp`, which hands the layer selection,
// the gunzip and the tar read to the daemon. Every consumer of this script already has Docker — the
// e2e harness drives Testcontainers and the CI leg runs on a Docker-capable runner — so speaking the
// registry protocol by hand bought no capability it does not already have.
//
// It DID carry one property worth naming, and this is the part of the change that is not a line cut.
// `assertBlobMatchesDigest` used to be the ONLY check that the bytes received were the bytes the
// registry attested — the hashes recorded in CoveExtraction.props cannot catch wrong upstream bytes,
// because they are computed FROM those bytes. That check now lives in the daemon, and it was confirmed
// against documentation before the guard was deleted rather than assumed from familiarity:
//
//   * moby's classic pull path (the one an overlay2 daemon and the CI runners use) streams each layer
//     through a digest verifier and fails with "filesystem layer verification failed for digest" on a
//     mismatch, and separately verifies the image config and manifest digests
//     — moby/moby, daemon/internal/distribution/pull_v2.go.
//   * containerd's path verifies at content-store commit: remotes.Fetch passes the descriptor's
//     expected digest to content.Copy, and Commit rejects content that does not hash to it
//     — containerd, core/remotes/handlers.go; docs/historical/design/data-flow.md states that pulling
//     verifies the manifest and every layer.
//
// Stated honestly, because it bounds the claim: the OCI distribution spec only says a client SHOULD
// verify a blob against the requested digest, not MUST. So this is an implementation property of both
// pull paths, not something the protocol forces — strong in practice, and worth re-checking if the
// extraction is ever moved onto a different puller.
//
// The consequence, stated because it is a real narrowing: assemblies mode now needs a Docker that can
// run LINUX containers. A macOS or Windows runner cannot, so those legs are permanently bare
// (`CoveSourceMode=none`).
//
// `--resolve-tags` is the one path that stays registry HTTP, because there is no `docker` verb for
// listing a remote repository's tags.
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

// The one member whose presence defines the layer. It is also what every consumer of the extraction
// needs, so an extraction that does not carry it is a failure rather than a smaller success.
const MARKER_MEMBER = "opt/cove/Cove.Data.dll";
const MEMBER_PREFIX = "opt/cove/";

// The `docker cp` source, derived from MEMBER_PREFIX rather than written again, so the path copied
// from and the member that proves the copy worked cannot drift apart. The trailing `/.` is what makes
// docker copy the directory's CONTENTS into the output directory, which is the layout the guarded
// assemblies are looked for in. A wrong path here yields an empty extraction rather than a wrong one,
// and the empty-extraction refusal below turns that into a failure rather than a smaller green run.
const CONTAINER_SOURCE = `/${MEMBER_PREFIX}.`;

// The assemblies the build's output-closure guard compares, and therefore the ones whose content this
// extraction has to record. Hashing every extracted file instead would go red whenever upstream
// legitimately changed an unrelated native asset under runtimes/, which is how a gate gets switched
// off; these four are the ones a NuGet copy can displace.
const GUARDED_ASSEMBLIES = ["Cove.Core.dll", "Cove.Data.dll", "Cove.Plugins.dll", "Cove.Sdk.dll"];

// Build output, imported by Directory.Build.targets, and the reason the version guard needs no
// hand-maintained constant.
const EXPECTATION_FILE = "CoveExtraction.props";

// The three stdout lines `.github/workflows/build.yml` reads back with line-start-anchored `grep -E`,
// declared once so the text has exactly one source rather than a literal per print site.
//
// These are a CONTRACT, not diagnostics. A rename, a re-order onto one line, or losing the
// leading-of-line position silently empties the shell variable that reads it, and the job's own guard
// only covers two of the three: an empty `informational` value degrades the notice with no failure
// anywhere. That asymmetry is why this object is pinned against the workflow's own greps in
// scripts/fetch-cove-assemblies.test.mjs, so drift on EITHER side goes red.
export const STDOUT_CONTRACT = Object.freeze({
  digest: "manifest digest: ",
  assemblyVersion: "Cove.Data.dll assembly version: ",
  informationalVersion: "Cove.Data.dll informational version: ",
});

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
 * Picks the `algorithm:hex` digest `docker image inspect` recorded for one specific repository.
 * <remarks>
 * `RepoDigests` carries one entry per repository the image is known under, so taking index 0 can
 * hand back a digest attributed to a DIFFERENT repository — a provenance value naming an image nobody
 * asked about. The entry is matched on the repository instead. Two distinct digests for the same
 * repository are refused rather than resolved by position: which one is current is genuinely
 * ambiguous, and guessing records a provenance value that cannot be checked.
 *
 * An absent entry is a throw. An image built locally and never pulled has no registry digest at all,
 * and passing an empty one down would fail inside `renderExtractionProps` far from the cause.
 * </remarks>
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

/**
 * Renders the two `Cove.Data.dll` version lines the workflow greps, in the workflow's own spelling.
 * <remarks>
 * An unreadable key prints `unreadable` rather than an empty value, so the line keeps its anchored
 * prefix and the notice says the version could not be read instead of silently reading as blank.
 * </remarks>
 */
export function renderVersionLines(versions) {
  return [
    `${STDOUT_CONTRACT.assemblyVersion}${versions["Assembly Version"] ?? "unreadable"}`,
    `${STDOUT_CONTRACT.informationalVersion}${versions.ProductVersion ?? "unreadable"}`,
  ];
}

/**
 * Refuses an extraction that wrote nothing or left no marker member, returning the marker's path.
 * <remarks>
 * The failure this exists for is a wrong container source path, which produces an EMPTY output
 * directory rather than a wrong one — and an empty extraction that returned quietly would surface as
 * a smaller green test run instead of a failure. fs-only and separated from the copy so both arms are
 * provable without Docker.
 * </remarks>
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
 * <remarks>
 * One missing assembly means the build's output-closure guard would have nothing to compare that
 * assembly against — a gate that silently covers three of four rather than a gate that fails.
 * </remarks>
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
 * <remarks>
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
 * </remarks>
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
 * <remarks>
 * Used only to decide whether the missing-feature refusal below applies, so its worst case is an
 * unnecessary refusal — loud — and never a silent skip: the quiet branch is taken solely when some
 * other entry point imported this module, which is exactly when doing nothing is correct.
 *
 * `import.meta.filename`, never a path read off a module URL's path component, for the same reason
 * `import.meta.dirname` is used above. Windows path casing is normalised because a drive letter
 * arrives in either case and a case-sensitive miss here would read as "not the entry point".
 *
 * Both sides are realpathed, and that is the whole reason this is not a plain compare of two resolved
 * strings: Node realpaths the module URL and leaves process.argv[1] as the caller spelled it, so an
 * invocation through a junction or a symlink — the shape this repository's documented worktree
 * workflow uses — compared unequal, and the refusal below then did not fire on exactly the runtime and
 * exactly the invocation that need it. Measured on v22.6.0 through a junction before the fix: zero
 * output, exit 0.
 * </remarks>
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

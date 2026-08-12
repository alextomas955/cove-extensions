// Behavior coverage for the Cove assembly extractor. Everything here runs offline: the registry is
// never contacted, and the layer-selection case drives an injected probe rather than a download, so
// a red here means the selection or the parsing is wrong and never that a CDN was slow.
//
// The exception is deliberate — one case reads this repo's REAL Directory.Build.props, so renaming
// either image property fails the validate job's `node --test scripts/*.test.mjs` glob instead of
// leaving the fetcher to read an empty value and fail somewhere further away.
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";

import {
  collectRegistryTags,
  compareSemver,
  flattenCoveMemberPath,
  orderLayerCandidates,
  parseMsBuildProperties,
  parseSemver,
  parseTarHeader,
  readCoveImageReference,
  readExtensionFloors,
  readTarMembers,
  readVersionStrings,
  resolveCoveLegs,
  selectLayerByContent,
  splitImageReference,
  splitReleaseChannels,
} from "./fetch-cove-assemblies.mjs";

const repoRoot = path.resolve(import.meta.dirname, "..");

// ---- fixtures ---------------------------------------------------------------------------------

function tarHeader({ name = "", size = 0, type = "0", prefix = "" } = {}) {
  const block = Buffer.alloc(512);
  block.write(name, 0, 100, "utf8");
  block.write(size.toString(8).padStart(11, "0") + "\0", 124, 12, "utf8");
  block.write(type, 156, 1, "utf8");
  block.write("ustar\0", 257, 6, "utf8");
  block.write(prefix, 345, 155, "utf8");
  return block;
}

function tarMember({ name, body = Buffer.alloc(0), type = "0", prefix = "" }) {
  const content = Buffer.isBuffer(body) ? body : Buffer.from(body, "utf8");
  const padded = Buffer.alloc(Math.ceil(content.length / 512) * 512);
  content.copy(padded);
  return Buffer.concat([tarHeader({ name, size: content.length, type, prefix }), padded]);
}

// ---- layer ordering and content-based selection ------------------------------------------------

test("layer candidates are probed largest first, and digest-less descriptors are dropped", () => {
  const ordered = orderLayerCandidates([
    { digest: "sha256:small", size: 100 },
    { size: 999_999 },
    { digest: "sha256:big", size: 84_600_000 },
    { digest: "sha256:middle", size: 5_000 },
  ]);

  assert.deepEqual(
    ordered.map((layer) => layer.digest),
    ["sha256:big", "sha256:middle", "sha256:small"],
  );
});

test("the layer is chosen by content, not by size, when two candidates are the same size", async () => {
  // The real amd64 manifest lists the /opt/cove layer TWICE under different digests, which is why
  // neither index nor size can identify it. Both candidates here are byte-identical in size and only
  // the second carries the marker.
  const candidates = [
    { digest: "sha256:decoy", size: 84_600_000 },
    { digest: "sha256:real", size: 84_600_000 },
  ];
  const probed = [];

  const chosen = await selectLayerByContent(candidates, async (candidate) => {
    probed.push(candidate.digest);
    return candidate.digest === "sha256:real";
  });

  assert.equal(chosen.digest, "sha256:real");
  assert.deepEqual(probed, ["sha256:decoy", "sha256:real"], "candidates are probed in order");
});

test("the first carrying layer wins and no later candidate is downloaded", async () => {
  const probed = [];
  const chosen = await selectLayerByContent(
    [
      { digest: "sha256:a", size: 3 },
      { digest: "sha256:b", size: 2 },
    ],
    async (candidate) => {
      probed.push(candidate.digest);
      return true;
    },
  );

  assert.equal(chosen.digest, "sha256:a");
  assert.deepEqual(probed, ["sha256:a"]);
});

test("no carrying layer is an error naming every layer searched, never an empty extraction", async () => {
  await assert.rejects(
    () =>
      selectLayerByContent(
        [
          { digest: "sha256:a", size: 1 },
          { digest: "sha256:b", size: 2 },
        ],
        async () => false,
      ),
    (error) => {
      assert.match(error.message, /opt\/cove\/Cove\.Data\.dll/);
      assert.match(error.message, /Searched 2 layer\(s\)/);
      assert.match(error.message, /sha256:a/);
      assert.match(error.message, /sha256:b/);
      return true;
    },
  );
});

// ---- tar member parsing ------------------------------------------------------------------------

test("a tar header yields its name, octal size and type flag", () => {
  const header = parseTarHeader(
    tarHeader({ name: "opt/cove/Cove.Data.dll", size: 2_629_632, type: "0" }),
  );

  assert.equal(header.name, "opt/cove/Cove.Data.dll");
  assert.equal(header.size, 2_629_632);
  assert.equal(header.type, "0");
});

test("a ustar prefix is joined onto the name", () => {
  const header = parseTarHeader(tarHeader({ name: "Cove.Data.dll", prefix: "opt/cove" }));
  assert.equal(header.name, "opt/cove/Cove.Data.dll");
});

test("the all-zero end-of-archive block reads as null", () => {
  assert.equal(parseTarHeader(Buffer.alloc(512)), null);
});

test("a base-256 size is refused rather than silently truncated", () => {
  const block = tarHeader({ name: "big", size: 0 });
  block[124] = 0x80;
  assert.throws(() => parseTarHeader(block), /base-256/);
});

test("the 512-byte padding advances the cursor, so an odd-sized member does not desynchronise the next", () => {
  // 10 bytes of content occupy a whole 512-byte block. Reading the declared size but advancing by the
  // padded size is the only way the second member's header lands where it is looked for.
  const archive = Buffer.concat([
    tarMember({ name: "opt/cove/first.txt", body: "0123456789" }),
    tarMember({ name: "opt/cove/second.txt", body: "second" }),
    Buffer.alloc(1024),
  ]);

  const members = [...readTarMembers(archive)];

  assert.deepEqual(
    members.map((member) => member.name),
    ["opt/cove/first.txt", "opt/cove/second.txt"],
  );
  assert.equal(members[0].body.toString("utf8"), "0123456789");
  assert.equal(members[1].body.toString("utf8"), "second");
});

test("directory entries are skipped and a GNU long name applies to the member that follows", () => {
  const longName =
    "opt/cove/runtimes/linux-x64/native/a-name-long-enough-to-need-its-own-record.so";
  const archive = Buffer.concat([
    tarMember({ name: "opt/cove/", type: "5" }),
    tarMember({ name: "././@LongLink", type: "L", body: `${longName}\0` }),
    tarMember({ name: "opt/cove/truncated", body: "payload" }),
    Buffer.alloc(1024),
  ]);

  const members = [...readTarMembers(archive)];

  assert.deepEqual(
    members.map((member) => member.name),
    [longName],
  );
});

// ---- member path flattening --------------------------------------------------------------------

test("opt/cove members are flattened, keeping any deeper structure", () => {
  assert.equal(flattenCoveMemberPath("opt/cove/Cove.Data.dll"), "Cove.Data.dll");
  assert.equal(flattenCoveMemberPath("./opt/cove/Cove.Data.dll"), "Cove.Data.dll");
  assert.equal(flattenCoveMemberPath("/opt/cove/Cove.Data.dll"), "Cove.Data.dll");
  assert.equal(
    flattenCoveMemberPath("opt/cove/runtimes/linux-x64/native/libe_sqlite3.so"),
    "runtimes/linux-x64/native/libe_sqlite3.so",
  );
});

test("anything outside opt/cove, and anything that would escape the output directory, is refused", () => {
  assert.equal(flattenCoveMemberPath("opt/other/thing.dll"), null);
  assert.equal(flattenCoveMemberPath("usr/lib/thing.dll"), null);
  assert.equal(flattenCoveMemberPath("opt/cove/"), null);
  assert.equal(flattenCoveMemberPath("opt/cove/../../etc/passwd"), null);
  assert.equal(
    flattenCoveMemberPath("opt/cove/.wh.Cove.Data.dll"),
    null,
    "overlay whiteout marker",
  );
});

// ---- image reference -----------------------------------------------------------------------------

test("an image reference splits into its registry host and repository", () => {
  assert.deepEqual(splitImageReference("ghcr.io/yourcove/cove-app"), {
    registry: "ghcr.io",
    repository: "yourcove/cove-app",
  });
});

test("a URL or a host-less reference is refused rather than defaulted to some other registry", () => {
  assert.throws(
    () => splitImageReference("https://ghcr.io/yourcove/cove-app"),
    /not a URL|not a url|URL/,
  );
  assert.throws(() => splitImageReference("cove-app"), /names no registry host/);
  assert.throws(() => splitImageReference(""), /empty/);
});

// ---- the seam with the real build file ------------------------------------------------------------

test("the real Directory.Build.props declares both image properties", () => {
  // Reads the repo's own build file, so renaming CoveTestImageRepository or CoveTestImageTag fails
  // here — where the fetcher takes them from — instead of drifting until a CI leg cannot resolve a tag.
  const propsPath = path.join(repoRoot, "Directory.Build.props");
  const props = parseMsBuildProperties(fs.readFileSync(propsPath, "utf8"));

  assert.ok(
    (props.CoveTestImageRepository ?? "") !== "",
    "Directory.Build.props must declare a non-empty CoveTestImageRepository",
  );
  assert.ok(
    (props.CoveTestImageTag ?? "") !== "",
    "Directory.Build.props must declare a non-empty CoveTestImageTag",
  );

  const reference = readCoveImageReference(propsPath);
  assert.equal(reference.repository, props.CoveTestImageRepository.split("/").slice(1).join("/"));
  assert.equal(reference.tag, props.CoveTestImageTag);
});

test("the property reader expands a $(Name) reference to the value already read", () => {
  const props = parseMsBuildProperties(`
    <Project><PropertyGroup>
      <CoveMinVersion>1.1.0</CoveMinVersion>
      <CoveSdkVersion Condition="'$(CoveSdkVersion)' == ''">$(CoveMinVersion)</CoveSdkVersion>
    </PropertyGroup></Project>
  `);

  assert.equal(props.CoveSdkVersion, "1.1.0");
});

// ---- version strings ------------------------------------------------------------------------------

test("the version-resource reader finds a key's UTF-16 value across its alignment padding", () => {
  const entry = (key, value) =>
    Buffer.concat([
      Buffer.from(`${key}\0`, "utf16le"),
      Buffer.alloc(2), // the 4-byte alignment padding a real version block carries
      Buffer.from(`${value}\0`, "utf16le"),
    ]);

  const buffer = Buffer.concat([
    Buffer.from("MZ  ", "utf8"),
    entry("Assembly Version", "1.1.1.0"),
    entry("ProductVersion", "1.1.1-dev.175"),
  ]);

  const versions = readVersionStrings(buffer);

  assert.equal(versions["Assembly Version"], "1.1.1.0");
  assert.equal(versions.ProductVersion, "1.1.1-dev.175");
  assert.equal(
    versions.FileVersion,
    undefined,
    "an absent key yields no entry rather than a guess",
  );
});

// ---- tag parsing, ranking and leg resolution ------------------------------------------------------

test("the strict-semver regex is the whole filter: every non-semver tag spelling parses to null", () => {
  // No denylist names `latest`, `nightly`, `sha-*` or the truncated `X.Y` aliases anywhere — the
  // regex rejects all of them, so an upstream tag convention nobody anticipated cannot leak in
  // through a list nobody updated.
  for (const spelling of ["latest", "nightly", "sha-deadbeef", "1.1"]) {
    assert.equal(parseSemver(spelling), null, spelling);
  }

  assert.deepEqual(parseSemver("1.1.0"), {
    tag: "1.1.0",
    major: 1,
    minor: 1,
    patch: 0,
    prerelease: [],
  });
  assert.deepEqual(parseSemver("1.3.0-rc.2").prerelease, ["rc", "2"]);
});

test("ranking follows semver precedence, including the three pre-release rules", () => {
  const ranked = [
    "1.1.0",
    "1.0.0-alpha.1",
    "1.2.0-rc.2",
    "1.0.0",
    "1.1.1-dev.179",
    "1.0.0-1",
    "1.3.0-rc.2",
    "1.1.0-rc.1",
    "1.1.1-dev.175",
    "1.0.0-alpha",
  ]
    .map(parseSemver)
    .sort(compareSemver)
    .map((parsed) => parsed.tag);

  assert.deepEqual(ranked, [
    "1.0.0-1", // a numeric identifier ranks BELOW an alphanumeric one
    "1.0.0-alpha",
    "1.0.0-alpha.1", // a longer pre-release outranks a shorter prefix of itself
    "1.0.0", // a release outranks every pre-release of the same version
    "1.1.0-rc.1",
    "1.1.0",
    "1.1.1-dev.175",
    "1.1.1-dev.179",
    "1.2.0-rc.2",
    "1.3.0-rc.2",
  ]);
});

test("the GA/pre-release split puts every tag carrying a pre-release component in the pre-release bucket and nothing else", () => {
  const { ga, prerelease } = splitReleaseChannels(
    ["1.0.0", "1.1.0", "1.3.0-rc.2", "1.1.1-dev.175", "0.9.0"].map(parseSemver),
  );

  assert.deepEqual(
    ga.map((parsed) => parsed.tag),
    ["0.9.0", "1.0.0", "1.1.0"],
    "GA ascending, so the newest is last",
  );
  assert.deepEqual(
    prerelease.map((parsed) => parsed.tag),
    ["1.1.1-dev.175", "1.3.0-rc.2"],
  );
});

test("the floor leg resolves to the exact floor tag the registry lists", () => {
  const resolved = resolveCoveLegs({
    floor: "1.1.0",
    tags: ["latest", "nightly", "1.0.0", "1.1.0", "1.3.0-rc.2"],
  });

  const floorLeg = resolved.legs.find((leg) => leg.role.split("+").includes("floor"));
  assert.equal(floorLeg.tag, "1.1.0");
  assert.equal(floorLeg.advisory, false);
  assert.equal(resolved.examined.tags, 5);
  assert.equal(resolved.examined.parsed, 3);
});

test("a floor tag absent from the registry's tag list is refused, never defaulted to something near it", () => {
  // A floor leg pointing at a tag that is not there would otherwise surface as an HTTP 404 deep
  // inside the extraction, long after the value that caused it was chosen.
  assert.throws(
    () => resolveCoveLegs({ floor: "1.2.0", tags: ["1.1.0", "1.2.0-rc.1", "1.2.0-rc.2"] }),
    (error) => {
      assert.match(error.message, /1\.2\.0/);
      assert.match(error.message, /not/);
      return true;
    },
  );
});

test("a floor that is not strict semver is refused before it can reach a registry URL", () => {
  assert.throws(() => resolveCoveLegs({ floor: "1.2", tags: ["1.1.0"] }), /1\.2/);
});

// ---- the seam with the real catalog ---------------------------------------------------------------

test("every real catalog entry reaches a minCoveVersion floor through its own manifestPath", () => {
  // minCoveVersion is NOT a catalog field; it lives in each entry's manifest, reached through
  // manifestPath. Reading the repo's real files here means a catalog entry that loses its manifest
  // path, or a manifest that loses its floor, fails the validate job's own node --test rather than
  // failing later as a leg with no version to resolve.
  const floors = readExtensionFloors();

  assert.ok(floors.length > 0, "extensions/catalog.json must declare at least one extension");
  for (const { floor, manifestPath } of floors) {
    assert.ok(
      parseSemver(floor) !== null,
      `${manifestPath} declares minCoveVersion '${floor}', which is not strict semver`,
    );
  }
});

// ---- paginated tag reading ------------------------------------------------------------------------

test("the tag reader follows Link: rel=next across pages and reports how many it read", async () => {
  const pages = {
    "/v2/x/tags/list": {
      tags: ["1.0.0", "1.1.0"],
      link: '</v2/x/tags/list?last=1.1.0&n=2>; rel="next"',
    },
    "/v2/x/tags/list?last=1.1.0&n=2": { tags: ["1.2.0"], link: "" },
  };

  const read = [];
  const result = await collectRegistryTags(async (pathAndQuery) => {
    read.push(pathAndQuery);
    return pages[pathAndQuery];
  }, "/v2/x/tags/list");

  assert.deepEqual(result.tags, ["1.0.0", "1.1.0", "1.2.0"]);
  assert.equal(result.pages, 2);
  assert.deepEqual(read, Object.keys(pages));
});

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
  flattenCoveMemberPath,
  orderLayerCandidates,
  parseMsBuildProperties,
  parseTarHeader,
  readCoveImageReference,
  readTarMembers,
  readVersionStrings,
  selectLayerByContent,
  splitImageReference,
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

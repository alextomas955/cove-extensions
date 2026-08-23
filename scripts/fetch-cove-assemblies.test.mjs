// Behavior coverage for the Cove assembly extractor. Everything here runs offline: neither the
// registry nor Docker is contacted, and the extraction's refusals are driven over temporary
// directories, so a red here means the logic is wrong and never that a CDN or a daemon was slow.
//
// Three cases deliberately read REAL repository files rather than fixtures, because each pins a seam
// where a copy would agree with itself forever while the other side drifted: Directory.Build.props
// (the image properties), extensions/catalog.json (each extension's declared floor) and
// tests/e2e/lib/harness.mjs (the helpers it imports from this module).
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import {
  assertExtractionNotEmpty,
  collectRegistryTags,
  compareSemver,
  main,
  parseMsBuildProperties,
  imageAtLeastVersion,
  parseSemver,
  readCoveImageReference,
  readExtensionFloors,
  readGuardedAssemblies,
  readVersionStrings,
  renderExtractionProps,
  renderVersionLines,
  resolveCoveLegs,
  selectRepoDigest,
  splitImageReference,
  splitReleaseChannels,
  STDOUT_CONTRACT,
} from "./fetch-cove-assemblies.mjs";

const repoRoot = path.resolve(import.meta.dirname, "..");

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
    Buffer.from("MZ\0\0", "utf8"),
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

test("a host-capability floor is compared, not enumerated: only tags below it read as lacking it", () => {
  const image = (tag) => `ghcr.io/yourcove/cove-app:${tag}`;
  // Cove publishes per-entity events for bulk mutations from 1.2.0 (issue #108).
  for (const tag of ["1.0.0", "1.1.0", "1.1.1", "0.9.0"])
    assert.equal(imageAtLeastVersion(image(tag), "1.2.0"), false, tag);
  for (const tag of ["1.2.0", "1.3.0", "1.10.0", "2.0.0"])
    assert.equal(imageAtLeastVersion(image(tag), "1.2.0"), true, tag);

  // A tag that is no version at all tracks ahead of the last release, so it counts as capable.
  for (const tag of ["nightly", "latest"])
    assert.equal(imageAtLeastVersion(image(tag), "1.2.0"), true, tag);

  // A prerelease sorts below its own release, so it reads as lacking the capability. That is a skip,
  // never a false failure, which is the direction to err in.
  assert.equal(imageAtLeastVersion(image("1.2.0-rc.1"), "1.2.0"), false);

  // The tag is the last colon-separated component, so a registry port is not mistaken for one.
  assert.equal(imageAtLeastVersion("localhost:5000/cove-app:1.0.0", "1.2.0"), false);

  // A floor that is not strict semver would silently admit everything, so it throws instead.
  assert.throws(() => imageAtLeastVersion(image("1.2.0"), "nightly"), /strict X\.Y\.Z floor/);
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

test("a tag list from which nothing parses as strict semver is refused, naming how many were read", () => {
  // Not an empty leg set: a registry that only ever answered with noise has told us nothing, and a
  // resolver that returned no legs from it would read as "this extension needs no version leg".
  assert.throws(
    () =>
      resolveCoveLegs({
        floor: "1.1.0",
        tags: ["latest", "nightly", "sha-abc123", "1.1"],
        source: "ghcr.io/o/r",
      }),
    (error) => {
      assert.match(error.message, /None of the 4 tag\(s\)/);
      assert.match(error.message, /ghcr\.io\/o\/r/);
      return true;
    },
  );
});

test("an empty tag list is refused, naming the registry and repository that was read", () => {
  assert.throws(() => resolveCoveLegs({ floor: "1.1.0", tags: [], source: "ghcr.io/o/r" }), {
    message: /ghcr\.io\/o\/r listed no tags at all/,
  });
});

test("a tag list that never stops advertising rel=next is refused at the page cap rather than looping", async () => {
  let served = 0;
  await assert.rejects(
    () =>
      collectRegistryTags(
        async () => {
          served += 1;
          return { tags: ["1.0.0"], link: '</v2/x/tags/list?last=1.0.0>; rel="next"' };
        },
        "/v2/x/tags/list",
        4,
      ),
    (error) => {
      assert.match(error.message, /after 4 page\(s\)/);
      assert.match(error.message, /cap of 4/);
      return true;
    },
  );
  assert.equal(served, 4, "the cap stops the loop rather than the loop stopping itself");
});

test("when the newest GA equals the floor, the two legs collapse onto one image and the roles merge", () => {
  const resolved = resolveCoveLegs({
    floor: "1.1.0",
    tags: ["1.0.0", "1.1.0", "1.2.0-rc.1", "1.3.0-rc.2", "latest"],
  });

  assert.deepEqual(resolved.legs, [
    { tag: "1.1.0", role: "floor+newest-ga", advisory: false },
    { tag: "1.3.0-rc.2", role: "newest-prerelease", advisory: true },
  ]);
  assert.equal(resolved.examined.roles, 3, "three roles resolved");
  assert.equal(resolved.legs.length, 2, "two distinct images");
});

test("a newest GA above the floor yields three legs and three distinct images", () => {
  const resolved = resolveCoveLegs({
    floor: "1.1.0",
    tags: ["1.1.0", "1.2.0", "1.3.0-rc.2"],
  });

  assert.deepEqual(resolved.legs, [
    { tag: "1.1.0", role: "floor", advisory: false },
    { tag: "1.2.0", role: "newest-ga", advisory: false },
    { tag: "1.3.0-rc.2", role: "newest-prerelease", advisory: true },
  ]);
  assert.equal(resolved.examined.roles, 3);
});

test("a floor above every published GA omits the newest-ga role rather than resolving it below the floor", () => {
  const resolved = resolveCoveLegs({
    floor: "1.3.0-rc.2",
    tags: ["1.0.0", "1.1.0", "1.2.0-rc.1", "1.3.0-rc.2", "latest"],
  });

  assert.deepEqual(resolved.legs, [
    { tag: "1.3.0-rc.2", role: "floor+newest-prerelease", advisory: false },
  ]);
  assert.equal(
    resolved.examined.roles,
    2,
    "a role with no subject at or above the floor is absent, not counted",
  );
});

// ---- the generated build expectation ---------------------------------------------------------------

test("the rendered expectation carries the tag, the digest and one Sha256 per assembly, in the form the build reads back", () => {
  const rendered = renderExtractionProps({
    tag: "1.1.0",
    digest: "sha256:abc123",
    assemblies: [
      { name: "Cove.Data.dll", sha256: "a".repeat(64) },
      { name: "Cove.Core.dll", sha256: "b".repeat(64) },
    ],
  });

  // Read back with the same property reader the fetcher uses on Directory.Build.props, so the
  // attributed form is pinned rather than assumed.
  const props = parseMsBuildProperties(rendered);
  assert.equal(props.CoveExtractionImageTag, "1.1.0");
  assert.equal(props.CoveExtractionManifestDigest, "sha256:abc123");
  assert.match(
    rendered,
    /<CoveExtractedAssembly Include="Cove\.Data\.dll" Sha256="a{64}" \/>/,
    "the expected hash rides as metadata on the item the build hashes",
  );
});

test("a value that could inject markup into a file the build imports is refused", () => {
  const assemblies = [{ name: "Cove.Data.dll", sha256: "a".repeat(64) }];
  assert.throws(
    () =>
      renderExtractionProps({
        tag: '1.1.0"/><Exec Command="whoami',
        digest: "sha256:ab",
        assemblies,
      }),
    /not a plain tag name/,
  );
  assert.throws(
    () => renderExtractionProps({ tag: "1.1.0", digest: "not-a-digest", assemblies }),
    /algorithm:hex digest/,
  );
  assert.throws(
    () => renderExtractionProps({ tag: "1.1.0", digest: "sha256:ab", assemblies: [] }),
    /recorded no assemblies/,
  );
  assert.throws(
    () =>
      renderExtractionProps({
        tag: "1.1.0",
        digest: "sha256:ab",
        assemblies: [{ name: "Cove.Data.dll", sha256: "A".repeat(64) }],
      }),
    /64 lowercase hex digits/,
  );
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

test("the three contract lines are rendered in the spelling captured off the real CI run", () => {
  // Transcribed by hand from a real CI run's output, not computed from the module under test: an
  // expectation derived from the code would agree with it forever.
  const digestLine = `${STDOUT_CONTRACT.digest}sha256:9365e9b1165b8134c899829401996f075ebd113447fe7933e652121bd6c4863c`;
  assert.equal(
    digestLine,
    "manifest digest: sha256:9365e9b1165b8134c899829401996f075ebd113447fe7933e652121bd6c4863c",
  );

  assert.deepEqual(renderVersionLines({ "Assembly Version": "1.1.0.0", ProductVersion: "1.1.0" }), [
    "Cove.Data.dll assembly version: 1.1.0.0",
    "Cove.Data.dll informational version: 1.1.0",
  ]);
});

test("an unreadable version key prints 'unreadable' rather than an empty value", () => {
  // An empty value would keep the prefix and read as a blank version in the notice; `unreadable` says
  // what happened.
  assert.deepEqual(renderVersionLines({}), [
    "Cove.Data.dll assembly version: unreadable",
    "Cove.Data.dll informational version: unreadable",
  ]);
});

// ---- the helpers tests/e2e/lib/harness.mjs imports ----------------------------------------------

test("the four helpers the e2e harness imports still resolve from this module", async () => {
  // Imported the way tests/e2e/lib/harness.mjs imports them, so an accidental un-export or a rename
  // goes red here rather than deep inside a Playwright run where the cause is much further away.
  const module = await import("./fetch-cove-assemblies.mjs");

  for (const name of [
    "compareSemver",
    "parseSemver",
    "readCoveImageReference",
    "readExtensionFloors",
  ]) {
    assert.equal(typeof module[name], "function", `${name} must stay exported for the e2e harness`);
  }

  // Read the harness's own import list, so adding a fifth import there without exporting it fails here.
  const harness = fs.readFileSync(
    path.join(repoRoot, "tests", "e2e", "lib", "harness.mjs"),
    "utf8",
  );
  const imported =
    /import \{([^}]+)\} from "\.\.\/\.\.\/\.\.\/scripts\/fetch-cove-assemblies\.mjs"/.exec(harness);
  assert.ok(imported !== null, "the harness must still import from this module by relative path");

  for (const name of imported[1]
    .split(",")
    .map((entry) => entry.trim())
    .filter(Boolean)) {
    assert.equal(
      typeof module[name],
      "function",
      `harness.mjs imports ${name}, which this module must export`,
    );
  }
});

// ---- extraction refusals -------------------------------------------------------------------------

function temporaryDirectory() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "fetch-cove-assemblies-"));
}

test("an extraction that wrote nothing is refused, never returned as a smaller success", () => {
  const directory = temporaryDirectory();
  assert.throws(() => assertExtractionNotEmpty(directory, 0), /wrote 0 file\(s\)/);
});

test("files written with no marker member is refused — the shape a wrong container path produces", () => {
  // `docker cp` from the wrong source path yields an empty or marker-less directory, so this is the
  // arm that turns a mistyped container path into a failure instead of a quietly smaller test run.
  const directory = temporaryDirectory();
  fs.writeFileSync(path.join(directory, "Something.Else.dll"), "x");
  assert.throws(() => assertExtractionNotEmpty(directory, 1), /left no Cove\.Data\.dll/);
});

test("a complete extraction returns the marker path it verified", () => {
  const directory = temporaryDirectory();
  fs.writeFileSync(path.join(directory, "Cove.Data.dll"), "x");
  assert.equal(assertExtractionNotEmpty(directory, 1), path.join(directory, "Cove.Data.dll"));
});

test("each guarded assembly missing on its own is refused, naming that assembly", () => {
  const guarded = ["Cove.Core.dll", "Cove.Data.dll", "Cove.Plugins.dll", "Cove.Sdk.dll"];

  // One arm per assembly: a loop that only ever dropped the first would leave three unproven.
  for (const absent of guarded) {
    const directory = temporaryDirectory();
    for (const name of guarded) {
      if (name !== absent) fs.writeFileSync(path.join(directory, name), name);
    }
    assert.throws(
      () => readGuardedAssemblies(directory),
      new RegExp(`left no ${absent.replaceAll(".", "\\.")}`),
      `${absent} absent must be refused`,
    );
  }

  const complete = temporaryDirectory();
  for (const name of guarded) fs.writeFileSync(path.join(complete, name), name);
  const read = readGuardedAssemblies(complete);
  assert.deepEqual(
    read.map((assembly) => assembly.name),
    guarded,
    "a complete extraction yields one entry per guarded assembly, in declaration order",
  );
  // Each body above is its own file name, and both digests below were produced by `sha256sum` outside
  // this file and transcribed by hand. That is the point: hashing the same input with node:crypto here
  // would compare the function under test against the primitive it calls and agree with it forever.
  assert.equal(
    read.find((assembly) => assembly.name === "Cove.Data.dll").sha256,
    "d783b667d2a145e9c94771e78658133f19e1ccb7ca9c66ef45a5d2ae8ce54c9c",
  );
  assert.equal(
    read.find((assembly) => assembly.name === "Cove.Core.dll").sha256,
    "4757d278843a37c603cb100bbafbc1a477bf1fc8c4105af1511559419ded4de2",
  );
});

// ---- the digest docker records -------------------------------------------------------------------

test("the RepoDigests entry is matched on the repository, never taken by position", () => {
  // Index 0 can belong to a DIFFERENT repository the same image is known under, which would record a
  // provenance digest naming an image nobody asked about.
  const digest = `sha256:${"b".repeat(64)}`;
  assert.equal(
    selectRepoDigest(
      [`docker.io/someone/else@sha256:${"a".repeat(64)}`, `ghcr.io/yourcove/cove-app@${digest}`],
      "ghcr.io/yourcove/cove-app",
    ),
    digest,
  );
});

test("no digest, no matching digest, and two digests for one repository are all refused", () => {
  assert.throws(() => selectRepoDigest([], "ghcr.io/yourcove/cove-app"), /no RepoDigests/);
  assert.throws(
    () =>
      selectRepoDigest(
        [`docker.io/other/img@sha256:${"a".repeat(64)}`],
        "ghcr.io/yourcove/cove-app",
      ),
    /None of docker's 1 RepoDigests/,
  );
  assert.throws(
    () =>
      selectRepoDigest(
        [
          `ghcr.io/yourcove/cove-app@sha256:${"a".repeat(64)}`,
          `ghcr.io/yourcove/cove-app@sha256:${"b".repeat(64)}`,
        ],
        "ghcr.io/yourcove/cove-app",
      ),
    /ambiguous/,
  );
});

test("a digest docker reports in a shape renderExtractionProps would reject is refused at the source", () => {
  assert.throws(
    () => selectRepoDigest(["ghcr.io/yourcove/cove-app@not-a-digest"], "ghcr.io/yourcove/cove-app"),
    /not an algorithm:hex digest/,
  );
});

// ---- the CLI's two mode refusals -----------------------------------------------------------------

// Driven through the real exported entry point rather than a private parser, so the refusal is proven
// where a caller meets it. Both throw before anything reaches the network or Docker.

test("--tag refuses a value that is not strict semver, before it can reach a registry URL", async () => {
  await assert.rejects(() => main(["--tag", "latest"]), /not a strict X\.Y\.Z semver/);
  await assert.rejects(() => main(["--tag", "1.1"]), /not a strict X\.Y\.Z semver/);
});

test("--tag together with --resolve-tags is refused: two modes, one argument", async () => {
  await assert.rejects(() => main(["--resolve-tags", "--tag", "1.1.0"]), /no meaning/);
  await assert.rejects(() => main(["--tag", "1.1.0", "--resolve-tags"]), /no meaning/);
});

test("an unrecognised argument is refused with the usage line rather than ignored", async () => {
  await assert.rejects(() => main(["--not-an-argument"]), /Unrecognised argument/);
});

// The extraction empties its target recursively, so an --out that CONTAINS the repository deletes the
// working tree. Driven through main for the reason the section header states, and the repo root is
// derived the way the script derives it rather than written down, so this cannot pass by naming a
// directory that is not the one the guard compares against.
//
// Every target is absolute: `--out .` is the realistic typo, but it resolves against the runner's
// working directory, so asserting on it would pass or fail for a reason that is not this guard. The
// case-varied forms are here because Windows hands a drive letter over in either case and a
// case-sensitive prefix comparison lets the destructive path straight through.
test("--out is refused when it is the repository or an ancestor of it", async () => {
  const repoRoot = path.resolve(import.meta.dirname, "..");
  const targets = [repoRoot, path.dirname(repoRoot), path.parse(repoRoot).root];

  if (process.platform === "win32") {
    targets.push(repoRoot.toLowerCase(), repoRoot.toUpperCase());
  }

  for (const target of targets) {
    await assert.rejects(
      () => main(["--out", target]),
      /Refusing to delete the working tree/,
      `--out '${target}' reaches a recursive delete of the repository`,
    );
  }
});

// Behavior coverage for the Cove version resolver. Everything here runs offline: the registry is never
// contacted, so a red here means the logic is wrong and never that a registry was slow.
//
// Two cases deliberately read real repository files rather than fixtures, because each pins a seam
// where a copy would agree with itself forever while the other side drifted: Directory.Build.props
// (the image repository) and tests/e2e/lib/harness.mjs (the helpers it imports from this module).
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";

import {
  collectRegistryTags,
  compareSemver,
  highestDeclaredFloor,
  imageAtLeastVersion,
  main,
  parseSemver,
  readCoveImageReference,
  readExtensionFloors,
  resolveCoveLegs,
  splitImageReference,
  splitReleaseChannels,
} from "./cove-versions.mjs";
import { parseMsBuildProperties } from "./repo-files.mjs";

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

test("the real Directory.Build.props declares the image repository", () => {
  // Reads the repo's own build file, so renaming CoveTestImageRepository fails here instead of drifting
  // until a CI leg cannot resolve a tag.
  const propsPath = path.join(repoRoot, "Directory.Build.props");
  const props = parseMsBuildProperties(fs.readFileSync(propsPath, "utf8"));

  assert.ok(
    (props.CoveTestImageRepository ?? "") !== "",
    "Directory.Build.props must declare a non-empty CoveTestImageRepository",
  );

  const reference = readCoveImageReference(propsPath);
  assert.equal(reference.repository, props.CoveTestImageRepository.split("/").slice(1).join("/"));
});

// ---- tag parsing, ranking and leg resolution ------------------------------------------------------

test("the strict-semver parser is the whole filter: every non-semver tag spelling parses to null", () => {
  // No denylist names `latest`, `nightly`, `sha-*` or the truncated `X.Y` aliases anywhere - the
  // parser rejects all of them, so an upstream tag convention nobody anticipated cannot leak in
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
  // A floor leg pointing at a tag that is not there would otherwise fail later, as an image pull or a
  // checkout, long after the value that caused it was chosen.
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

test("the four helpers the e2e harness imports still resolve from this module", async () => {
  // Imported the way tests/e2e/lib/harness.mjs imports them, so an accidental un-export or a rename
  // goes red here rather than deep inside a Playwright run where the cause is much further away.
  const module = await import("./cove-versions.mjs");

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
  const imported = /import \{([^}]+)\} from "\.\.\/\.\.\/\.\.\/scripts\/cove-versions\.mjs"/.exec(
    harness,
  );
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

// ---- the command line ----------------------------------------------------------------------------

test("an unrecognised argument is refused with the usage line rather than ignored", async () => {
  await assert.rejects(() => main(["--not-an-argument"]), /Unrecognised argument/);
});

test("the offline modes print exactly what the workflows read off stdout", () => {
  // lint.yml and sonar.yml append --cove-ref's stdout to $GITHUB_OUTPUT, and build.yml parses
  // --floors-only's stdout as JSON, so anything else on stdout would corrupt the step's output.
  const script = path.join(import.meta.dirname, "cove-versions.mjs");
  const run = (flag) => spawnSync(process.execPath, [script, flag], { encoding: "utf8" });
  const declared = readExtensionFloors();

  const ref = run("--cove-ref");
  assert.equal(ref.status, 0, ref.stderr);
  assert.equal(ref.stdout, `ref=v${highestDeclaredFloor(declared).floor}\n`);

  const floors = run("--floors-only");
  assert.equal(floors.status, 0, floors.stderr);
  assert.deepEqual(
    JSON.parse(floors.stdout).include.map((leg) => [leg.extension.id, leg.cove]),
    declared.map(({ entry, floor }) => [entry.id, { tag: floor, role: "floor", advisory: false }]),
  );
});

test("highestDeclaredFloor picks the highest declared floor by semver, not by string order", () => {
  const declared = [
    { entry: { name: "a" }, floor: "1.9.9" },
    { entry: { name: "b" }, floor: "1.10.0" },
    { entry: { name: "c" }, floor: "1.2.3" },
  ];
  assert.equal(highestDeclaredFloor(declared).floor, "1.10.0");
});

test("highestDeclaredFloor ranks a release above a prerelease of the same version", () => {
  const declared = [
    { entry: { name: "a" }, floor: "2.0.0-rc.1" },
    { entry: { name: "b" }, floor: "2.0.0" },
  ];
  assert.equal(highestDeclaredFloor(declared).floor, "2.0.0");
});

test("highestDeclaredFloor throws on an empty list rather than resolving a ref nothing declared", () => {
  assert.throws(() => highestDeclaredFloor([]), /No extension floor was declared/);
});

test("highestDeclaredFloor throws on a lone unparseable floor, which a reduce alone would return unchecked", () => {
  // A one-entry list never invokes a reduce callback, so validating inside one would pass this
  // through and the caller would check out `vnot-a-version`.
  assert.throws(
    () => highestDeclaredFloor([{ entry: { name: "solo" }, floor: "not-a-version" }]),
    /solo declares a floor that is not a semver/,
  );
});

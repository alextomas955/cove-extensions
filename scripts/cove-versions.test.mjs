// Behavior coverage for the Cove version resolver. Everything here runs offline: the registry is never
// contacted, so a red here means the logic is wrong and never that a registry was slow.
//
// One case deliberately reads the real Directory.Build.props rather than a fixture, because it pins a
// seam where a copy would agree with itself forever while the build file drifted.
import { test } from "node:test";
import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";

import {
  collectRegistryTags,
  compareSemver,
  highestDeclaredFloor,
  main,
  parseSemver,
  readCoveImageReference,
  readImageRevision,
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
  assert.throws(() => splitImageReference("https://ghcr.io/yourcove/cove-app"), /not a URL/);
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

  assert.doesNotThrow(() => readCoveImageReference(propsPath));
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

test("a floor tag absent from the registry's tag list is refused, never defaulted to something near it", () => {
  // A floor leg pointing at a tag that is not there would otherwise fail later, as an image pull or a
  // checkout, long after the value that caused it was chosen.
  assert.throws(
    () => resolveCoveLegs({ floor: "1.2.0", tags: ["1.1.0", "1.2.0-rc.1", "1.2.0-rc.2"] }),
    (error) => {
      assert.match(error.message, /1\.2\.0/);
      assert.match(error.message, /is not published on .+ as an exact tag/);
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

// One arrange: a declared floor and the tag list a registry answered with, against the legs the
// resolver must make of them. `examined` carries only the counters a row has something to say about;
// a role with no subject at or above the floor is absent rather than counted, so the role count is
// what separates an omitted role from a merged one.
for (const { name, floor, tags, legs, examined } of [
  {
    name: "the floor leg is the exact floor tag, and a newest GA equal to it collapses the two roles onto one image",
    floor: "1.1.0",
    tags: ["latest", "nightly", "1.0.0", "1.1.0", "1.3.0-rc.2"],
    legs: [
      { tag: "1.1.0", role: "floor+newest-ga", advisory: false },
      { tag: "1.3.0-rc.2", role: "newest-prerelease", advisory: true },
    ],
    examined: { tags: 5, parsed: 3, roles: 3 },
  },
  {
    name: "a newest GA above the floor yields three legs and three distinct images",
    floor: "1.1.0",
    tags: ["1.1.0", "1.2.0", "1.3.0-rc.2"],
    legs: [
      { tag: "1.1.0", role: "floor", advisory: false },
      { tag: "1.2.0", role: "newest-ga", advisory: false },
      { tag: "1.3.0-rc.2", role: "newest-prerelease", advisory: true },
    ],
    examined: { roles: 3 },
  },
  {
    name: "a floor above every published GA omits the newest-ga role rather than resolving it below the floor",
    floor: "1.3.0-rc.2",
    tags: ["1.0.0", "1.1.0", "1.2.0-rc.1", "1.3.0-rc.2", "latest"],
    legs: [{ tag: "1.3.0-rc.2", role: "floor+newest-prerelease", advisory: false }],
    examined: { roles: 2 },
  },
  {
    name: "a newest pre-release that sorts below the newest GA is not a leg, since that release superseded it",
    floor: "1.5.0",
    tags: ["1.4.1", "1.4.2-dev.188", "1.5.0"],
    legs: [{ tag: "1.5.0", role: "floor+newest-ga", advisory: false }],
    examined: { roles: 2 },
  },
]) {
  test(name, () => {
    const resolved = resolveCoveLegs({ floor, tags });

    assert.deepEqual(resolved.legs, legs);
    for (const [counter, value] of Object.entries(examined)) {
      assert.equal(resolved.examined[counter], value, counter);
    }
  });
}

const AMD64 = `sha256:${"a".repeat(64)}`;
const CONFIG = `sha256:${"c".repeat(64)}`;

test("an image's source commit is read from its revision label, through an index to the linux/amd64 image", async () => {
  const revision = "f74f4dd2aa42389d0dc689122b2fd842aef62b15";
  const bodies = {
    "/v2/o/app/manifests/1.5.1-dev.3": {
      manifests: [
        { digest: `sha256:${"b".repeat(64)}`, platform: { os: "linux", architecture: "arm64" } },
        { digest: AMD64, platform: { os: "linux", architecture: "amd64" } },
      ],
    },
    [`/v2/o/app/manifests/${encodeURIComponent(AMD64)}`]: { config: { digest: CONFIG } },
    [`/v2/o/app/blobs/${encodeURIComponent(CONFIG)}`]: {
      config: { Labels: { "org.opencontainers.image.revision": revision } },
    },
  };

  const read = [];
  const found = await readImageRevision(
    async (pathAndQuery) => {
      read.push(pathAndQuery);
      return bodies[pathAndQuery];
    },
    "o/app",
    "1.5.1-dev.3",
  );

  assert.equal(found, revision);
  assert.deepEqual(read, Object.keys(bodies));
});

test("a malformed digest, a missing or partial revision label, or an index with no linux/amd64 image is refused", async () => {
  const single = (configDigest, labels) =>
    readImageRevision(
      async (pathAndQuery) =>
        pathAndQuery.includes("/blobs/")
          ? { config: { Labels: labels } }
          : { config: { digest: configDigest } },
      "o/app",
      "1.5.1-dev.3",
    );
  const label = (value) => ({ "org.opencontainers.image.revision": value });

  await assert.rejects(() => single("sha256:../../x", label("f".repeat(40))), TypeError);
  await assert.rejects(() => single(CONFIG, {}), /no org\.opencontainers\.image\.revision label/);
  await assert.rejects(() => single(CONFIG, label("main")), /full commit hash/);
  await assert.rejects(
    () =>
      readImageRevision(
        async () => ({
          manifests: [{ digest: AMD64, platform: { os: "linux", architecture: "arm64" } }],
        }),
        "o/app",
        "1.5.1-dev.3",
      ),
    /no linux\/amd64 image/,
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

// ---- the command line ----------------------------------------------------------------------------

test("an unrecognised argument is refused with the usage line rather than ignored", async () => {
  await assert.rejects(() => main(["--not-an-argument"]), /Unrecognised argument/);
});

// The floors the committed files declare, read straight off catalog.json and each extension.json
// rather than through the module under test: a floor the module computed would agree with the stdout
// the module printed however wrong both were.
function committedFloors() {
  const catalogPath = path.join(repoRoot, "extensions", "catalog.json");
  const catalog = JSON.parse(fs.readFileSync(catalogPath, "utf8"));
  return catalog.extensions.map((entry) => ({
    entry,
    manifestPath: entry.manifestPath,
    floor: JSON.parse(fs.readFileSync(path.join(repoRoot, entry.manifestPath), "utf8"))
      .minCoveVersion,
  }));
}

test("the offline modes print exactly what the workflows read off stdout", () => {
  // the checkout-cove action appends --cove-ref's stdout to $GITHUB_OUTPUT, and ci.yml parses
  // --floors-only's stdout as JSON, so anything else on stdout would corrupt the step's output.
  const script = path.join(import.meta.dirname, "cove-versions.mjs");
  const run = (flag) => spawnSync(process.execPath, [script, flag], { encoding: "utf8" });
  const declared = committedFloors();

  const ref = run("--cove-ref");
  assert.equal(ref.status, 0, ref.stderr);
  assert.match(ref.stdout, /^ref=v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\n$/);
  for (const { entry, floor, manifestPath } of declared) {
    assert.ok(
      ref.stderr.includes(`${entry.name} declares floor ${floor} (from ${manifestPath})`),
      `the per-extension report must reach stderr, got: ${ref.stderr}`,
    );
  }

  const release = spawnSync(process.execPath, [script, "--source-ref", "1.5.0"], {
    encoding: "utf8",
  });
  assert.equal(release.status, 0, release.stderr);
  assert.equal(release.stdout, "ref=v1.5.0\n", "a release reads no registry and is its v-tag");

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

// Local, reproducible proof of an extension's <tagPrefix>v<semver> release path. Given a release
// tag, it runs the same build -> strip-verify -> stamp -> version-parity -> package sequence
// build.yml's release job runs, targeting exactly the extension the tag resolves to, then asserts
// the produced zip is isolated to that extension (its own assembly, no other extension's assembly,
// no host-provided assembly, no absolute-path leak).
//
// Real release tags stay off the development branch, so this stands in for pushing a tag: it proves
// the path works without publishing anything.
//
// Cove SDK source: CI's release job builds against the pinned NuGet Cove.Sdk (no monorepo sibling on
// the runner). Locally that pinned package predates the host API the extensions compile against, so
// a local build needs the sibling ../cove source. This harness resolves the source with the same
// precedence the repo's Directory.Build wiring uses — explicit flag > COVE_REPO > ../cove sibling >
// NuGet — so the one documented command builds in both environments, and it prints which source it
// used. The strip-verify, stamp, parity, package, and isolation steps are identical either way.
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import process from "node:process";
import { spawnSync } from "node:child_process";

import { verifyPublishSet } from "./strip-verify.mjs";

const root = path.resolve(import.meta.dirname, "..");
const SEMVER = /^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;

function fail(message) {
  console.error("PROVE FAIL: " + message);
  process.exit(1);
}

function run(command, args, options = {}) {
  const result = spawnSync(command, args, { stdio: "inherit", ...options });
  if (result.status !== 0) {
    fail(command + " " + args.join(" ") + " exited " + (result.status ?? "signal " + result.signal));
  }
  return result;
}

// Resolves whether the local build uses the ../cove sibling source. Mirrors the repo's build-wiring
// precedence; returns null (use pinned NuGet, CI-identical) when no local source is available.
function resolveUseLocalCove() {
  const flag = process.argv.slice(3).find((a) => a === "--local-cove" || a === "--nuget-cove");
  if (flag === "--local-cove") return true;
  if (flag === "--nuget-cove") return false;
  if (process.env.COVE_REPO && fs.existsSync(process.env.COVE_REPO)) return true;
  if (fs.existsSync(path.join(root, "..", "cove"))) return true;
  return false;
}

function main() {
  const tag = process.argv[2];
  if (!tag) fail("usage: node scripts/prove-release-path.mjs <tagPrefix>v<semver> [--local-cove|--nuget-cove]");

  const catalog = JSON.parse(fs.readFileSync(path.join(root, "extensions", "catalog.json"), "utf8"));
  const denylist = JSON.parse(fs.readFileSync(path.join(root, ".github", "DLL_DENYLIST.json"), "utf8"));

  // 1. Resolve the tag to exactly one catalog entry — the same startsWith(tagPrefix) filter
  //    build.yml's validate job uses. Zero or many is a fatal ambiguity.
  const matches = catalog.extensions.filter((entry) => tag.startsWith(entry.tagPrefix));
  if (matches.length !== 1) {
    fail("tag " + tag + " must match exactly one catalog tagPrefix, matched " + matches.length);
  }
  const entry = matches[0];
  const versionTag = tag.slice(entry.tagPrefix.length);
  if (!SEMVER.test(versionTag)) fail("tag version suffix " + versionTag + " is not valid semver");

  // 2. Version is the semver suffix without its leading v (matching build.yml's derive step).
  const version = versionTag.slice(1);

  const useLocalCove = resolveUseLocalCove();
  const coveSourceLabel = useLocalCove ? "local ../cove sibling" : "pinned NuGet Cove.Sdk";
  console.log("Resolved tag " + tag + " -> " + entry.id + " (version " + version + ")");
  console.log("Cove SDK source: " + coveSourceLabel);

  const workDir = fs.mkdtempSync(path.join(os.tmpdir(), "prove-release-"));
  const outDir = path.join(workDir, entry.name);

  // 3. Publish exactly the resolved entry's project into a clean temp dir.
  run("dotnet", [
    "publish",
    path.join(root, entry.projectPath),
    "-c",
    "Release",
    "-o",
    outDir,
    "-p:UseLocalCoveSource=" + useLocalCove,
    "-p:Version=" + version,
    "-p:ContinuousIntegrationBuild=true",
  ]);

  // 4. Shared strip-verify — the same gate CI runs.
  const stripResult = verifyPublishSet({
    dir: outDir,
    entryName: entry.name,
    requiredBundledDlls: entry.requiredBundledDlls ?? [],
    denylist,
  });
  if (!stripResult.ok) {
    for (const f of stripResult.failures) console.error("  " + f);
    fail("strip-verify rejected the publish set");
  }

  // 5. Copy the manifest into the publish dir and stamp its version, as build.yml does.
  const packagedManifest = path.join(outDir, "extension.json");
  fs.copyFileSync(path.join(root, entry.manifestPath), packagedManifest);
  const manifest = JSON.parse(fs.readFileSync(packagedManifest, "utf8"));
  manifest.version = version;
  fs.writeFileSync(packagedManifest, JSON.stringify(manifest, null, 2) + "\n");

  // 6. Version parity across the source manifest / C# source / UI package. The registry-manifest
  //    4th arg is omitted — this extension has none (build.yml now omits it too when absent).
  run("node", [
    path.join(root, "scripts", "check-version-parity.mjs"),
    path.join(root, entry.manifestPath),
    path.join(root, entry.versionSourcePath),
    path.join(root, entry.uiPath, "package.json"),
  ]);

  // 7. Package the publish dir into <id>-<version>.zip, the same naming build.yml uses.
  const zipName = entry.id + "-" + version + ".zip";
  const zipPath = path.join(workDir, zipName);
  run("bash", ["-c", 'cd "$1" && zip -r "$2" . >/dev/null', "bash", outDir, zipPath]);

  // 8. Isolation assertions against what actually landed in the zip.
  const listed = spawnSync("unzip", ["-Z1", zipPath], { encoding: "utf8" });
  if (listed.status !== 0) fail("could not list zip contents");
  const zipEntries = listed.stdout.split(/\r?\n/).filter(Boolean).map((p) => path.basename(p));

  const problems = [];
  if (!zipEntries.includes(entry.name + ".dll")) problems.push("missing " + entry.name + ".dll");
  if (!zipEntries.includes("extension.json")) problems.push("missing extension.json");
  const otherExtensionAssemblies = catalog.extensions
    .filter((e) => e.id !== entry.id)
    .map((e) => e.name + ".dll")
    .filter((dll) => zipEntries.includes(dll));
  if (otherExtensionAssemblies.length) problems.push("other extension assembly present: " + otherExtensionAssemblies.join(", "));
  const leakedHostDlls = zipEntries.filter((f) => f.endsWith(".dll") && denylist.includes(f.slice(0, -".dll".length)));
  if (leakedHostDlls.length) problems.push("host-provided assembly present: " + leakedHostDlls.join(", "));
  if (problems.length) {
    for (const p of problems) console.error("  " + p);
    fail("isolation assertions violated");
  }

  // 9. PASS report.
  console.log("");
  console.log("RELEASE-PATH PROOF: PASS");
  console.log("  resolved extension: " + entry.id);
  console.log("  version:            " + version);
  console.log("  package:            " + zipName);
  console.log("  approved publish set:");
  for (const f of stripResult.approved) console.log("    " + f);
  process.exit(0);
}

main();

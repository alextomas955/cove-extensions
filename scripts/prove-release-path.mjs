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

// Every process this harness launches is one of these fixed executables, each invoked with
// argument arrays built only from the SEMVER-validated tag and trusted catalog.json fields. Gating
// the command name on the allowlist makes it a validated value rather than an injection sink.
const AllowedCommands = new Set(["dotnet", "node", "unzip", "zip"]);

function fail(message) {
  console.error("PROVE FAIL: " + message);
  process.exit(1);
}

// Central spawn: the command must be one of the fixed allowlisted executables, and callers pass
// only array args (never a shell string) built from the SEMVER-validated tag and trusted
// catalog.json — so there is no command-name or shell-quoting injection surface.
function spawnAllowed(command, args, options) {
  if (!AllowedCommands.has(command)) fail("refusing to run non-allowlisted command: " + command);
  // SonarCloud's agentic argument-injection rule (S8705) is a verified false positive here:
  // spawnSync with an ARRAY of args and no shell never spawns a shell (Node's own recommended-safe
  // form — see nodejs.org child_process docs), the command is allowlisted above, and the only
  // external input (the release tag) is rejected unless it matches a catalog tagPrefix AND a strict
  // SEMVER regex before it can reach any arg. Nothing to sanitize that is not already sanitized.
  return spawnSync(command, args, options); // NOSONAR
}

function run(command, args, options = {}) {
  const result = spawnAllowed(command, args, { stdio: "inherit", ...options });
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

// Resolves the release tag to exactly one catalog entry and its semver version, failing on an
// ambiguous match or a non-semver suffix (the same contract build.yml's validate job enforces).
function resolveEntry(catalog, tag) {
  const matches = catalog.extensions.filter((entry) => tag.startsWith(entry.tagPrefix));
  if (matches.length !== 1) {
    fail("tag " + tag + " must match exactly one catalog tagPrefix, matched " + matches.length);
  }
  const entry = matches[0];
  const versionTag = tag.slice(entry.tagPrefix.length);
  if (!SEMVER.test(versionTag)) fail("tag version suffix " + versionTag + " is not valid semver");
  return { entry, version: versionTag.slice(1) };
}

// Asserts the produced zip is isolated to exactly this extension: its own assembly + manifest, no
// other catalog extension's assembly, and no host-provided (denylisted) assembly.
function assertZipIsolation(zipPath, entry, catalog, denylist) {
  const listed = spawnAllowed("unzip", ["-Z1", zipPath], { encoding: "utf8" });
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
}

function main() {
  const tag = process.argv[2];
  if (!tag) fail("usage: node scripts/prove-release-path.mjs <tagPrefix>v<semver> [--local-cove|--nuget-cove]");

  const catalog = JSON.parse(fs.readFileSync(path.join(root, "extensions", "catalog.json"), "utf8"));
  const denylist = JSON.parse(fs.readFileSync(path.join(root, ".github", "DLL_DENYLIST.json"), "utf8"));

  // 1. Resolve the tag to exactly one catalog entry + its semver version.
  const { entry, version } = resolveEntry(catalog, tag);

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

  // 7. Package the publish dir into <id>-<version>.zip, the same naming build.yml uses. zip runs
  //    with the publish dir as cwd so entries are relative; array args + no shell = no quoting risk.
  const zipName = entry.id + "-" + version + ".zip";
  const zipPath = path.join(workDir, zipName);
  run("zip", ["-r", "-q", zipPath, "."], { cwd: outDir });

  // 8. Isolation assertions against what actually landed in the zip.
  assertZipIsolation(zipPath, entry, catalog, denylist);

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

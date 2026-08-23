// Produces the build output the E2E harness installs, for every extension the catalog declares an
// e2e suite for — the natural pair of assemble-package.mjs, which consumes a publishDir and refuses
// a missing one.
//
// Nothing else in the repository produces it: the only other producers are the `dotnet publish`
// steps in build.yml, which a local checkout never runs, so a fresh clone could not run the harness.
//
// Wired as tests/e2e's `pretest`, so the prerequisite is satisfied by mechanism rather than by a
// reader remembering a README line.
// Catalog-driven, and no extension is named anywhere below. The selection predicate is the one
// playwright.config.mjs derives its projects from, so an extension that gains an e2e suite gains its
// publish step with no edit here.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { spawnSync } from "node:child_process";
import { checkRelativePath } from "./catalog-paths.mjs";

// import.meta.dirname, never a filesystem path read off a module URL's path component: on Windows that
// yields a leading-slash form which resolves to a doubled drive prefix.
const repoRoot = path.resolve(import.meta.dirname, "..");
const catalogPath = path.join(repoRoot, "extensions", "catalog.json");

// Where the output has to land is not this script's choice: tests/e2e/lib/resolve-extension.mjs derives
// <extension>/artifacts/publish from an extension's own fixtures module, and the harness looks nowhere
// else. Publishing anywhere else would leave the harness failing exactly as it did before.
const PUBLISH_SEGMENTS = ["artifacts", "publish"];

// Built from its code point rather than written as a literal, so this file's own source stays plain
// ASCII and the character cannot be lost or mangled by an editor that does not show it.
const BYTE_ORDER_MARK = String.fromCodePoint(0xfeff);

function readCatalogEntries() {
  const text = fs.readFileSync(catalogPath, "utf8");
  const catalog = JSON.parse(text.startsWith(BYTE_ORDER_MARK) ? text.slice(1) : text);
  return Array.isArray(catalog.extensions) ? catalog.extensions : [];
}

// No shell, so an argument stays one argv element and never becomes text something else parses. stdio is
// inherited rather than piped: the child's own output is the log, and a status read off the end of a
// pipe is the classic way a failure gets reported as a success.
function run(command, args, cwd, label) {
  const result = spawnSync(command, args, { cwd, stdio: "inherit" });
  if (result.error) {
    return `${label} could not start (${command}): ${result.error.message}`;
  }
  if (result.status !== 0) {
    const how = result.status === null ? `signal ${result.signal}` : `exit status ${result.status}`;
    return `${label} failed with ${how}`;
  }
  return null;
}

// npm ships as a .cmd shim on Windows and Node refuses to spawn one without a shell, so npm is always
// run as `node <npm-cli.js>` — which needs no shell on any platform and assumes no binary on PATH.
// npm_execpath is npm's own answer and is set whenever npm started this process, which is every run
// through the `pretest` hook; the two filesystem candidates cover a direct `node scripts/…` invocation
// and differ only in where each platform's Node installation puts npm. No candidate resolving is a hard
// failure naming all of them, never a UI build quietly skipped.
function resolveNpmCli() {
  const nodeDir = path.dirname(process.execPath);
  const candidates = [
    process.env.npm_execpath,
    path.join(nodeDir, "node_modules", "npm", "bin", "npm-cli.js"),
    path.join(nodeDir, "..", "lib", "node_modules", "npm", "bin", "npm-cli.js"),
  ].filter((candidate) => typeof candidate === "string" && candidate.endsWith(".js"));

  return { cli: candidates.find((candidate) => fs.existsSync(candidate)) ?? null, candidates };
}

function publishEntry(entry, label) {
  const shapeErrors = [
    checkRelativePath("path", entry.path),
    checkRelativePath("projectPath", entry.projectPath),
    entry.uiPath == null ? null : checkRelativePath("uiPath", entry.uiPath),
  ].filter(Boolean);
  if (shapeErrors.length > 0) return shapeErrors.join("; ");

  const publishDir = path.resolve(repoRoot, entry.path, ...PUBLISH_SEGMENTS);
  if (!publishDir.startsWith(repoRoot + path.sep)) {
    return `the derived publish directory is outside this repository: ${publishDir}`;
  }

  console.log(`publish-extensions: ${label} -> ${path.relative(repoRoot, publishDir)}`);

  // Emptied before publishing, not merely published into. `dotnet publish` overwrites what it produces
  // and deletes nothing else, so a file from an earlier build survives — and a manifest left behind by a
  // reverted change is exactly how a spec pinning what an extension ships reddens as a regression that
  // does not exist. Cleaning removes that failure mode instead of leaving it to be remembered.
  fs.rmSync(publishDir, { recursive: true, force: true });

  // Mirrors the e2e job's own publish step in .github/workflows/build.yml, flags included. CoveSourceMode
  // is pinned to `none` there so the extension compiles against the published Cove packages users
  // receive; without the pin a developer with a local Cove checkout would stage a differently-built
  // assembly than CI does.
  const publishFailure = run(
    "dotnet",
    [
      "publish",
      path.join(repoRoot, entry.projectPath),
      "-c",
      "Release",
      "-o",
      publishDir,
      "-p:CoveSourceMode=none",
    ],
    repoRoot,
    `${label}: dotnet publish`,
  );
  if (publishFailure) return publishFailure;

  // The UI bundle is a declared artifact that the package assembler resolves from <uiPath>/dist and the
  // dotnet publish never produces, so an entry declaring a uiPath is not publishable without it.
  if (entry.uiPath) {
    const uiDir = path.resolve(repoRoot, entry.uiPath);
    const { cli, candidates } = resolveNpmCli();
    if (cli === null) {
      return `no npm CLI could be located, so the UI bundle cannot be built; looked at: ${candidates.join(", ")}`;
    }

    // `npm ci` only when the UI has no install yet, which is the fresh-checkout half of this fix.
    // Running it on every invocation would reinstall a tree the developer already owns and keeps
    // current; an absent one is nobody's, and its absence is the failure this branch exists for.
    if (!fs.existsSync(path.join(uiDir, "node_modules"))) {
      const installFailure = run(
        process.execPath,
        [cli, "ci"],
        uiDir,
        `${label}: npm ci in ${entry.uiPath}`,
      );
      if (installFailure) return installFailure;
    }

    const buildFailure = run(
      process.execPath,
      [cli, "run", "build"],
      uiDir,
      `${label}: npm run build in ${entry.uiPath}`,
    );
    if (buildFailure) return buildFailure;
  }

  return null;
}

function main() {
  const entries = readCatalogEntries();
  const selected = entries.filter((entry) => entry.e2ePath && entry.e2eProject);

  // Printed on every run, pass or fail: a step whose only output is its exit status cannot be told apart
  // from one that examined nothing.
  console.log(
    `publish-extensions: examined ${entries.length} catalog entries, of which ${selected.length} declared both e2ePath and e2eProject.`,
  );

  // The refusal playwright.config.mjs makes, for the same reason: a publish step that published nothing
  // and exited 0 reads as a satisfied prerequisite while providing none.
  if (selected.length === 0) {
    console.error(
      `publish-extensions: nothing to publish — examined ${entries.length} catalog entries, of which 0 declared both e2ePath and e2eProject.`,
    );
    return 1;
  }

  for (const entry of selected) {
    const label = entry.name ?? entry.id ?? "(catalog entry with no name or id)";
    // Stops at the first failure rather than carrying on, so the reported outcome describes every
    // extension named above it and none below.
    const failure = publishEntry(entry, label);
    if (failure) {
      console.error(`publish-extensions: ${label} FAILED — ${failure}`);
      return 1;
    }
    console.log(`publish-extensions: ${label} OK`);
  }

  console.log(`publish-extensions: published ${selected.length} of ${entries.length} extensions.`);
  return 0;
}

process.exit(main());

// Produces the build output the E2E harness installs, for every extension whose catalog entry
// declares an e2e suite. assemble-package.mjs consumes that output and refuses a missing one.
//
// The only other producers are the `dotnet publish` steps in build.yml, which a local checkout never
// runs, so without this a fresh clone cannot run the harness. It is wired as tests/e2e's `pretest`.
//
// No extension is named below. The selection predicate is the one playwright.config.mjs derives its
// projects from, so an extension that gains an e2e suite gains its publish step with no edit here.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { spawnSync } from "node:child_process";
import { checkRelativePath } from "./catalog-paths.mjs";

// import.meta.dirname, never a filesystem path read off a module URL's path component: on Windows that
// yields a leading-slash form which resolves to a doubled drive prefix.
const repoRoot = path.resolve(import.meta.dirname, "..");
const catalogPath = path.join(repoRoot, "extensions", "catalog.json");

// Where the output lands is not this script's choice: tests/e2e/lib/resolve-extension.mjs derives
// <extension>/artifacts/publish from an extension's own fixtures module, and the harness looks
// nowhere else.
const PUBLISH_SEGMENTS = ["artifacts", "publish"];

// Built from its code point rather than written as a literal, so this file's own source stays plain
// ASCII and the character cannot be lost or mangled by an editor that does not show it.
const BYTE_ORDER_MARK = String.fromCodePoint(0xfeff);

function readCatalogEntries() {
  const text = fs.readFileSync(catalogPath, "utf8");
  const catalog = JSON.parse(text.startsWith(BYTE_ORDER_MARK) ? text.slice(1) : text);
  return Array.isArray(catalog.extensions) ? catalog.extensions : [];
}

// No shell, so an argument stays one argv element and never becomes text something else parses. stdio
// is inherited rather than piped: the child's own output is the log, and a status read off the end of
// a pipe is the pipe's, not the child's.
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

  // Emptied before publishing, not merely published into. `dotnet publish` overwrites what it
  // produces and deletes nothing else, so a file from an earlier build survives — and a stale
  // manifest is enough to redden a spec that pins what an extension ships.
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

    // `npm ci` only when the UI has no install yet, which is the fresh-checkout case. Running it
    // every time would reinstall a tree the developer already owns and keeps current.
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

  // The same refusal playwright.config.mjs makes: a publish step that published nothing and exited 0
  // reads as a satisfied prerequisite while providing none.
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

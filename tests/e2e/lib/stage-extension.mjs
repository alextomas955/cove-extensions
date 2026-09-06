// Stages an extension into a folder shaped the way Cove's bind-mount install expects:
// <stagingRoot>/<extensionId>/{extension.json, *.dll, index.mjs, ...}.
//
// The folder is produced by the repo's own package assembler, the same one a release and a local dev
// deploy run, so a test installs the declared package: a file that would not ship cannot reach a
// passing test, and one that must ship cannot be missing from it.
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { assemblePackage, resolveEntryManifestPath } from "../../../scripts/assemble-package.mjs";

// Directories under an extension's source tree that hold build output rather than source.
const NOT_SOURCE = new Set(["bin", "obj", "dist", "node_modules"]);

/** The newest source file under `dir`, as `{ path, mtimeMs }`, or null when there is none. */
function newestSource(dir) {
  let newest = null;
  for (const item of readdirSync(dir, { withFileTypes: true })) {
    const here = join(dir, item.name);
    if (item.isDirectory()) {
      if (NOT_SOURCE.has(item.name)) continue;
      const found = newestSource(here);
      if (found && (!newest || found.mtimeMs > newest.mtimeMs)) newest = found;
      continue;
    }
    const { mtimeMs } = statSync(here);
    if (!newest || mtimeMs > newest.mtimeMs) newest = { path: here, mtimeMs };
  }
  return newest;
}

export function stageExtension({ repoRoot, publishDir, manifestPath, stagingRoot }) {
  if (!existsSync(publishDir)) {
    throw new Error(`stageExtension: publishDir does not exist: ${publishDir}`);
  }
  if (!existsSync(manifestPath)) {
    throw new Error(`stageExtension: manifestPath does not exist: ${manifestPath}`);
  }

  const manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  if (!manifest.id) {
    throw new Error(`stageExtension: manifest at ${manifestPath} has no "id" field`);
  }
  if (!manifest.version) {
    throw new Error(`stageExtension: manifest at ${manifestPath} has no "version" field`);
  }

  // The manifest read above names the staged directory and stamps the version, while assemblePackage
  // resolves the manifest to ship independently, from the catalog entry's own manifestPath. Should
  // those two diverge, the container receives a directory named for this manifest's id holding the
  // other one's document, and nothing fails.
  const packagedManifestPath = resolveEntryManifestPath(repoRoot, manifest.id);
  if (packagedManifestPath === null) {
    throw new Error(
      `stageExtension: no catalog entry matches id "${manifest.id}" from ${manifestPath}, so nothing declares what to package.`,
    );
  }
  if (resolve(packagedManifestPath) !== resolve(manifestPath)) {
    throw new Error(
      `stageExtension: the manifest read for id/version is not the one that would be packaged. ` +
        `Read: ${resolve(manifestPath)}. Catalog entry "${manifest.id}" declares: ${resolve(packagedManifestPath)}.`,
    );
  }

  // Catches a run that installs an assembly older than the code it is testing. The publish directory
  // is refreshed only by scripts/publish-extensions.mjs, wired as this package's npm pretest, while
  // the JS bundle is taken from the UI build output — so invoking Playwright directly stages a fresh
  // bundle on top of whatever assembly was left behind. An old assembly throws nothing. It serves a
  // manifest missing whatever the newer source declares, and the run then reads as a host that does
  // not do something rather than an artifact that is out of date.
  // The manifest sits in the project the entryDll is built from, so its directory is that assembly's
  // own source.
  const sourceRoot = dirname(manifestPath);
  const assembly = join(publishDir, manifest.entryDll);
  const newest = existsSync(assembly) ? newestSource(sourceRoot) : null;
  if (newest && statSync(assembly).mtimeMs < newest.mtimeMs) {
    throw new Error(
      `stageExtension: ${assembly} was built before ${newest.path}, so this run would install a backend that predates its own source. ` +
        `Run "node scripts/publish-extensions.mjs", or run the suite through "npm test" in tests/e2e, whose pretest does it.`,
    );
  }

  const target = join(stagingRoot, manifest.id);
  const result = assemblePackage({
    root: repoRoot,
    publishDir,
    packageDir: target,
    idOrName: manifest.id,
    version: manifest.version,
  });
  if (!result.ok) {
    throw new Error(
      `stageExtension: assembling ${manifest.id} failed: ${result.failures.join("; ")}`,
    );
  }

  return { id: manifest.id, path: target };
}

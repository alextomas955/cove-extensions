// Stages an extension into a folder shaped the way Cove's bind-mount install expects:
// <stagingRoot>/<extensionId>/{extension.json, *.dll, index.mjs, ...}.
//
// The staged folder is produced by the repo's own package assembler, the same one a release and a
// local dev deploy run, so what a test installs is the declared package rather than an approximation
// of it — a file that would not ship cannot reach a passing test, and one that must ship cannot be
// missing from it.
import { existsSync, readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { assemblePackage, resolveEntryManifestPath } from "../../../scripts/assemble-package.mjs";

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

  // The manifest read above names the staged directory and stamps the version, but assemblePackage
  // resolves the manifest to SHIP independently, from the catalog entry's own manifestPath. Two
  // readers of one fact: let them diverge and the container receives a directory named for this
  // manifest's id holding the other one's document, with nothing failing. Asserting they are the
  // same file is what keeps the second reader from being a second declaration.
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

// Stages a built extension's publish output into a folder shaped the way Cove's
// bind-mount install expects: <stagingRoot>/<extensionId>/{extension.json, *.dll, index.mjs, ...}.
// This mirrors what Renamer's own scripts/deploy-dev.ps1 does for a native install, but targets a
// throwaway staging dir instead of a live COVE_HOME.
import { cpSync, existsSync, mkdirSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';

export function stageExtension({ publishDir, manifestPath, uiBundlePath, stagingRoot }) {
  if (!existsSync(publishDir)) {
    throw new Error(`stageExtension: publishDir does not exist: ${publishDir}`);
  }
  if (!existsSync(manifestPath)) {
    throw new Error(`stageExtension: manifestPath does not exist: ${manifestPath}`);
  }

  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  if (!manifest.id) {
    throw new Error(`stageExtension: manifest at ${manifestPath} has no "id" field`);
  }

  const target = join(stagingRoot, manifest.id);
  if (existsSync(target)) {
    rmSync(target, { recursive: true, force: true });
  }
  mkdirSync(target, { recursive: true });

  cpSync(publishDir, target, { recursive: true });
  cpSync(manifestPath, join(target, 'extension.json'));

  if (uiBundlePath) {
    if (!existsSync(uiBundlePath)) {
      throw new Error(`stageExtension: uiBundlePath does not exist: ${uiBundlePath}`);
    }
    cpSync(uiBundlePath, join(target, 'index.mjs'));
  }

  return { id: manifest.id, path: target };
}

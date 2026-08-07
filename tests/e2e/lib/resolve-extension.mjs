// Resolves an extension's build outputs from the calling fixture's OWN module URL — the one place
// that encodes the `extensions/<Ext>/e2e/lib/…` layout, so per-extension fixtures never hand-roll a
// fixed-distance-to-repo-root path. Replaces the old `repoRoot = join(__dirname, '..','..','..','..')`
// anti-pattern (which broke the moment the harness or extensions/ folder moved).
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

/**
 * @param {string} callerUrl - `import.meta.url` of an extension's fixtures file, which lives at
 *   `…/extensions/<Ext>/e2e/lib/<name>.mjs`. Paths are derived self-relatively from this URL (two
 *   `..` hops reach the extension root), never from a guessed repo root.
 * @param {{ srcProject: string }} opts - the extension's .NET project name
 *   (→ `src/<srcProject>/extension.json`).
 * @returns {{ repoRoot: string, publishDir: string, manifestPath: string }} `repoRoot` is what the
 *   package assembler reads `extensions/catalog.json` from; the shipped set — the UI bundle included
 *   — is declared there, so no UI path is resolved here.
 */
export function resolveExtensionPaths(callerUrl, { srcProject }) {
  const here = dirname(fileURLToPath(callerUrl)); // …/extensions/<Ext>/e2e/lib
  const extRoot = join(here, '..', '..'); // …/extensions/<Ext>
  return {
    repoRoot: join(extRoot, '..', '..'),
    publishDir: join(extRoot, 'artifacts', 'publish'),
    manifestPath: join(extRoot, 'src', srcProject, 'extension.json'),
  };
}

// Resolves an extension's build outputs from the calling fixture's OWN module URL. This is the one
// place that encodes the `extensions/<Ext>/e2e/lib/…` layout, so a per-extension fixture never
// hand-rolls a fixed-distance-to-repo-root path.
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

/**
 * @param {string} callerUrl - `import.meta.url` of an extension's fixtures file, which must live at
 *   `…/extensions/<Ext>/e2e/lib/<name>.mjs` for the hops below to land.
 * @param {{ srcProject: string }} opts - the extension's .NET project name
 *   (→ `src/<srcProject>/extension.json`).
 * @returns {{ repoRoot: string, publishDir: string, manifestPath: string }} `repoRoot` is where the
 *   package assembler reads `extensions/catalog.json`, which declares the shipped set — so the UI
 *   bundle has no path here.
 */
export function resolveExtensionPaths(callerUrl, { srcProject }) {
  const here = dirname(fileURLToPath(callerUrl)); // …/extensions/<Ext>/e2e/lib
  const extRoot = join(here, "..", ".."); // …/extensions/<Ext>
  return {
    repoRoot: join(extRoot, "..", ".."),
    publishDir: join(extRoot, "artifacts", "publish"),
    manifestPath: join(extRoot, "src", srcProject, "extension.json"),
  };
}

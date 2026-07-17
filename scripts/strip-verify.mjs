// Shared, catalog-driven strip-verify gate: proves a publish set carries no host-provided
// (denylisted) assembly, carries the extension's own entry assembly plus any first-party runtime
// dependency it deliberately bundles, and leaks no absolute build path in its json. Both CI
// (build.yml) and the local release-proof harness (prove-release-path.mjs) call this one
// implementation so there is no drift between what CI enforces and what a local run proves.
//
// The host-assembly denylist is the single shared source at .github/DLL_DENYLIST.json. The
// per-extension "must be bundled" set comes from the catalog entry's requiredBundledDlls field
// (Renamer bundles System.IO.Hashing for its cross-volume mover; an extension that bundles no
// first-party runtime dependency declares [] and this check is a no-op).
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { pathToFileURL } from "node:url";

const root = path.resolve(import.meta.dirname, "..");

// Absolute-path markers are constructed from parts rather than written as bare literals so a
// downstream scan of this script's own source does not mistake the markers for a real leak.
const BACKSLASH = String.fromCodePoint(92);
const WINDOWS_DRIVE_ROOT = new RegExp("[A-Za-z]:" + BACKSLASH + BACKSLASH);
const UNIX_HOME_PREFIXES = ["/" + "home" + "/", "/" + "Users" + "/"];

/**
 * Runs the strip-verify checks against a publish directory.
 *
 * @param {object} opts
 * @param {string} opts.dir - the publish directory to scan.
 * @param {string} opts.entryName - the catalog entry name; its `<name>.dll` must be present.
 * @param {string[]} [opts.requiredBundledDlls] - first-party runtime deps that must ship (no `.dll`).
 * @param {string[]} opts.denylist - host-provided assembly names that must NOT ship (no `.dll`).
 * @returns {{ ok: boolean, failures: string[], approved: string[] }}
 */
export function verifyPublishSet({ dir, entryName, requiredBundledDlls = [], denylist }) {
  const failures = [];
  const files = fs.existsSync(dir) ? fs.readdirSync(dir) : [];

  for (const file of files) {
    if (!file.endsWith(".dll")) continue;
    const base = file.slice(0, -".dll".length);
    if (denylist.includes(base)) {
      failures.push("LEAK: host-provided assembly present: " + file);
    }
  }

  if (!files.includes(entryName + ".dll")) {
    failures.push(
      "MISSING: " + entryName + ".dll is absent from the publish set — build produced no extension assembly.",
    );
  }

  for (const name of requiredBundledDlls) {
    if (!files.includes(name + ".dll")) {
      failures.push("MISSING: " + name + ".dll is absent — a required bundled runtime dependency did not ship.");
    }
  }

  for (const file of files) {
    if (!file.endsWith(".json")) continue;
    const lines = fs.readFileSync(path.join(dir, file), "utf8").split(/\r?\n/);
    lines.forEach((line, index) => {
      const hit = WINDOWS_DRIVE_ROOT.test(line) || UNIX_HOME_PREFIXES.some((prefix) => line.includes(prefix));
      if (hit) {
        failures.push("LEAK: absolute path found in publish-set json: " + file + ":" + (index + 1) + ": " + line.trim());
      }
    });
  }

  return { ok: failures.length === 0, failures, approved: files.slice().sort() };
}

function resolveEntry(catalog, idOrName) {
  return catalog.extensions.find((entry) => entry.id === idOrName || entry.name === idOrName);
}

function main(argv) {
  const [dir, idOrName] = argv;
  if (!dir || !idOrName) {
    console.error("Usage: node scripts/strip-verify.mjs <publishDir> <extensionIdOrName>");
    return 1;
  }

  const catalog = JSON.parse(fs.readFileSync(path.join(root, "extensions", "catalog.json"), "utf8"));
  const entry = resolveEntry(catalog, idOrName);
  if (!entry) {
    console.error("No catalog entry matches id/name: " + idOrName);
    return 1;
  }

  const denylist = JSON.parse(fs.readFileSync(path.join(root, ".github", "DLL_DENYLIST.json"), "utf8"));
  const result = verifyPublishSet({
    dir,
    entryName: entry.name,
    requiredBundledDlls: entry.requiredBundledDlls ?? [],
    denylist,
  });

  if (!result.ok) {
    for (const failure of result.failures) console.error(failure);
    console.error("Strip-verify FAILED.");
    return 1;
  }

  console.log("Strip-verify PASS — approved publish set:");
  for (const file of result.approved) console.log("  " + file);
  return 0;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  process.exit(main(process.argv.slice(2)));
}

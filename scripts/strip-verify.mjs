// Shared, catalog-driven strip-verify gate: proves a package carries no host-provided (denylisted)
// assembly, carries the extension's own entry assembly plus any first-party runtime dependency it
// deliberately bundles, leaks no absolute build path in its json, and ships a manifest that
// describes the release actually being cut and the files actually present. The CI build job
// (build.yml) is its only caller, so this file is the whole of that check.
//
// It runs on the ASSEMBLED package — after the manifest and the frontend bundle have been copied in
// and the manifest version stamped — because the manifest assertions have no subject before that.
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

// The manifest fields that name a file the host loads out of the installed package. Each is
// optional except entryDll (checked separately); a declared one must resolve to a real file inside
// the package.
const BUNDLE_FIELDS = ["entryDll", "jsBundle", "cssBundle"];

const BOM = String.fromCodePoint(0xfeff);

function stripBom(text) {
  return text.startsWith(BOM) ? text.slice(BOM.length) : text;
}

// The bundle-path shape rejection, built from parts for the same reason as the leak markers above.
const PATH_ESCAPE_PREFIX = new RegExp("^([A-Za-z]:|[/" + BACKSLASH + BACKSLASH + "])");
const PATH_SEPARATORS = new RegExp("[/" + BACKSLASH + BACKSLASH + "]");

// A manifest bundle field is a package-relative name the host resolves next to the manifest, so an
// absolute path or a `..` segment would send it reading outside the installed extension directory.
// Rejected on shape before existence is even asked, since a path that escapes cannot be made safe
// by happening to exist.
function checkBundlePath(dir, field, value, failures) {
  if (typeof value !== "string" || value === "") {
    failures.push("INVALID: extension.json " + field + " must be a non-empty string, found: " + JSON.stringify(value));
    return;
  }

  const escapes = PATH_ESCAPE_PREFIX.test(value) || value.split(PATH_SEPARATORS).includes("..");
  if (escapes) {
    failures.push("ESCAPE: extension.json " + field + " must be a package-relative path, found: " + value);
    return;
  }

  if (!fs.existsSync(path.join(dir, value))) {
    failures.push("MISSING: extension.json " + field + " declares " + value + ", which is absent from the package.");
  }
}

// The packaged manifest is what the host reads at install time, so it — not the source copy — is
// what must name this release and these files. A package with no manifest cannot be loaded at all,
// which makes its absence a hard failure rather than a check with nothing to do.
function checkManifest(dir, expectedId, expectedVersion, failures) {
  const manifestPath = path.join(dir, "extension.json");
  if (!fs.existsSync(manifestPath)) {
    failures.push("MISSING: extension.json is absent from the package — the host cannot load an extension without its manifest.");
    return;
  }

  let manifest;
  try {
    manifest = JSON.parse(stripBom(fs.readFileSync(manifestPath, "utf8")));
  } catch (error) {
    failures.push("INVALID: packaged extension.json is not parseable JSON: " + error.message);
    return;
  }

  if (manifest.id !== expectedId) {
    failures.push("MISMATCH: packaged extension.json id is " + JSON.stringify(manifest.id) + ", expected " + JSON.stringify(expectedId) + ".");
  }

  if (manifest.version !== expectedVersion) {
    failures.push("MISMATCH: packaged extension.json version is " + JSON.stringify(manifest.version) + ", expected " + JSON.stringify(expectedVersion) + ".");
  }

  if (manifest.entryDll == null) {
    failures.push("MISSING: packaged extension.json declares no entryDll.");
  }

  for (const field of BUNDLE_FIELDS) {
    if (manifest[field] != null) checkBundlePath(dir, field, manifest[field], failures);
  }
}

/**
 * Runs the strip-verify checks against an assembled package directory.
 *
 * @param {object} opts
 * @param {string} opts.dir - the package directory to scan.
 * @param {string} opts.entryName - the catalog entry name; its `<name>.dll` must be present.
 * @param {string} opts.expectedId - the catalog entry id the packaged manifest must declare.
 * @param {string} opts.expectedVersion - the release version the packaged manifest must declare.
 * @param {string[]} [opts.requiredBundledDlls] - first-party runtime deps that must ship (no `.dll`).
 * @param {string[]} opts.denylist - host-provided assembly names that must NOT ship (no `.dll`).
 * @returns {{ ok: boolean, failures: string[], approved: string[] }}
 */
export function verifyPublishSet({ dir, entryName, expectedId, expectedVersion, requiredBundledDlls = [], denylist }) {
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

  checkManifest(dir, expectedId, expectedVersion, failures);

  return { ok: failures.length === 0, failures, approved: files.slice().sort() };
}

function resolveEntry(catalog, idOrName) {
  return catalog.extensions.find((entry) => entry.id === idOrName || entry.name === idOrName);
}

function main(argv) {
  const [dir, idOrName, expectedVersion] = argv;
  // expectedVersion is required rather than defaulted: a default would let a caller that forgot to
  // pass the release version still exit 0, which is the shape of a gate that inspects nothing.
  if (!dir || !idOrName || !expectedVersion) {
    console.error("Usage: node scripts/strip-verify.mjs <packageDir> <extensionIdOrName> <expectedVersion>");
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
    expectedId: entry.id,
    expectedVersion,
    requiredBundledDlls: entry.requiredBundledDlls ?? [],
    denylist,
  });

  if (!result.ok) {
    for (const failure of result.failures) console.error(failure);
    console.error("Strip-verify FAILED.");
    return 1;
  }

  console.log("Strip-verify PASS — " + entry.id + " " + expectedVersion + ", approved package contents:");
  for (const file of result.approved) console.log("  " + file);
  return 0;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  process.exit(main(process.argv.slice(2)));
}

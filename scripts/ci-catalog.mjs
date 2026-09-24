// The questions CI asks of extensions/catalog.json, answered in one tested place instead of in inline
// scripts inside the workflow files.
//
//   matrix <ref>        the build matrix: every entry, or only the tagged one on a release tag
//   release-tag <ref>   refuses a tag whose version disagrees with the manifest or the registry row
//   legs <ref> <file>   filters the Cove version legs in <file> to the entries `matrix` selects
//   list <field>        one line per entry declaring the field, failing when none does
import path from "node:path";
import process from "node:process";
import fs from "node:fs";
import { readJson } from "./repo-files.mjs";

const repoRoot = path.resolve(import.meta.dirname, "..");
const TAG_REF_PREFIX = "refs/tags/";
const RELEASE_VERSION = /^v\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;

function readEntries(root) {
  const catalog = readJson(path.join(root, "extensions", "catalog.json"));
  return Array.isArray(catalog.extensions) ? catalog.extensions : [];
}

/** The entries a run builds: all of them, or on a release tag only the one whose prefix it carries. */
export function selectEntries(entries, ref) {
  if (!ref.startsWith(TAG_REF_PREFIX)) return entries;
  const tag = ref.slice(TAG_REF_PREFIX.length);
  return entries.filter((entry) => tag.startsWith(entry.tagPrefix));
}

/**
 * Checks a release tag against the catalog, the manifest and the registry manifest, returning the
 * summary line on success and throwing with the reason otherwise.
 */
export function checkReleaseTag(root, ref) {
  const tag = ref.slice(TAG_REF_PREFIX.length);
  const matches = selectEntries(readEntries(root), ref);
  if (!ref.startsWith(TAG_REF_PREFIX) || matches.length !== 1) {
    throw new Error(
      `Tag ${tag} must match exactly one catalog tagPrefix, matched ${matches.length}.`,
    );
  }
  const [entry] = matches;
  const versionTag = tag.slice(entry.tagPrefix.length);
  if (!RELEASE_VERSION.test(versionTag)) {
    throw new Error(
      `Tag version suffix ${versionTag} is not valid semver (expected v<major>.<minor>.<patch>).`,
    );
  }
  const version = versionTag.slice(1);

  const manifestPath = entry.manifestPath ?? path.posix.join(entry.path, "extension.json");
  const manifest = readJson(path.join(root, manifestPath));
  if (manifest.version !== version) {
    throw new Error(
      `Tag ${tag} releases ${version}, but ${manifestPath} declares version ${manifest.version}. Set that version to ${version}, or retag.`,
    );
  }

  // An extension not yet published to the store has no registry manifest, and that is not a defect.
  const registryPath =
    entry.registryManifestPath ?? path.posix.join(entry.path, "extensions", `${entry.id}.json`);
  if (!fs.existsSync(path.join(root, registryPath))) {
    return `Tag ${tag} releases ${entry.id} ${version}; ${manifestPath} agrees; no registry manifest at ${registryPath}, so no versions[] row was checked.`;
  }
  const registry = readJson(path.join(root, registryPath));
  const latest = Array.isArray(registry.versions) ? registry.versions[0] : null;
  if (latest?.version !== version) {
    const found =
      latest == null ? "there are no versions[] rows" : `versions[0] is ${latest.version}`;
    throw new Error(
      `Registry manifest ${registryPath} does not describe this release: prepend a versions[] row for ${version} (${found}). Do not edit an existing row, since each one describes an immutable published artifact.`,
    );
  }
  return `Tag ${tag} releases ${entry.id} ${version}; ${manifestPath} agrees; ${registryPath} versions[0] is ${version}.`;
}

/** Keeps the legs whose extension the run builds, refusing an empty result. */
export function filterLegs(legs, entries, ref) {
  const selected = new Set(selectEntries(entries, ref).map((entry) => entry.id));
  const include = legs.include.filter((leg) => selected.has(leg.extension.id));
  if (include.length === 0) {
    throw new Error(
      "The Cove version axis resolved no leg for any selected extension, so the test jobs would run against nothing.",
    );
  }
  return { include };
}

/** Each entry's value for `field`, skipping entries that do not declare it, refusing an empty result. */
export function listField(entries, field) {
  const values = entries.map((entry) => entry[field]).filter(Boolean);
  if (values.length === 0) {
    throw new Error(
      `No catalog entry declares ${field}, so the caller would iterate over nothing.`,
    );
  }
  return values;
}

const USAGE =
  "Usage: ci-catalog.mjs matrix <ref> | release-tag <ref> | legs <ref> <file> | list <field>";

export function main(argv, root = repoRoot) {
  const [command, argument, ...rest] = argv;
  const arity = command === "legs" ? 1 : 0;
  if (argument === undefined || rest.length !== arity) throw new Error(USAGE);
  const entries = readEntries(root);
  switch (command) {
    case "matrix":
      process.stdout.write(`${JSON.stringify({ extension: selectEntries(entries, argument) })}\n`);
      return 0;
    case "release-tag":
      console.log(checkReleaseTag(root, argument));
      return 0;
    case "legs": {
      const result = filterLegs(readJson(rest[0]), entries, argument);
      for (const leg of result.include) {
        console.error(`cove leg: ${leg.extension.name} ${leg.cove.role} ${leg.cove.tag}`);
      }
      process.stdout.write(`${JSON.stringify(result)}\n`);
      return 0;
    }
    case "list":
      for (const value of listField(entries, argument)) console.log(value);
      return 0;
    default:
      throw new Error(USAGE);
  }
}

if (import.meta.main) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(`ci-catalog: ${error.message}`);
    process.exitCode = 1;
  }
}

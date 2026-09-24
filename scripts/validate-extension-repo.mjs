// Fork of yourcove/multi-extension-repo-template's scripts/validate-extension-repo.mjs.
// Forked on: 2026-07-01
// Upstream diff base: https://github.com/yourcove/multi-extension-repo-template/blob/main/scripts/validate-extension-repo.mjs
//
// How this fork differs from upstream, so a later sync knows what was deliberate:
//
// 1. Reads the additive projectPath/manifestPath catalog fields when present, rather than always
// deriving {path}/{name}.csproj by convention. Additive, not a replacement: an entry without them
// still validates through the upstream convention path.
//
// 2. Drops upstream's floor check on the two package-version properties, because neither has a
// subject here - the SDK version is $(CoveMinVersion), so the comparison asks whether a value is at
// least itself, and no Cove.Core property is declared at all. The per-entry extension.json
// comparison, which does have a subject, survives.
//
// 3. Reports a count per check rather than only the entry count. Exit 0 alone cannot distinguish a
// check that passed from one that never ran, which is how the checks removed in #2 stayed invisible.
//
// 4. Confirms every optional catalog path the CI matrix consumes exists on disk. A typo in one is
// otherwise caught late and cryptically inside a matrix leg.
//
// 5. Asserts every C# project the catalog implies is declared in CoveExtensions.slnx. The formatting
// and analyzer gates take their subject list from that solution, so a project missing from it is
// never compiled and the gate reports success over a smaller set than the reader believes. Detection
// rather than generation, and one-way: the solution may hold projects the catalog does not describe.
//
// 6. Compares the registry manifest's minCoveVersion against extension.json's, reading only the
// versions[] row matching the version extension.json declares. A registry row describes an immutable
// published zip, so its floor is that zip's, not the source tree's; releasing.md forbids editing a
// published row.
//
// 7. Drops upstream's manifest-only entries (`manifestOnly`, `kind` bundle or scraper-pack). Every
// entry here ships an assembly, so every entry must declare entryDll and a project CI can build.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { compareSemver, parseSemver } from "./cove-versions.mjs";
import { parseMsBuildProperties, readJson } from "./repo-files.mjs";

// root is the parent of this file's own scripts/ directory - matching upstream's template
// exactly. A real extensions/ subfolder lives one level below the repo root and holds
// catalog.json plus one directory per extension (root/extensions/<Name>), while
// Directory.Build.props/.targets stay at the repo root alongside scripts/. This single-level
// climb is now genuinely correct (not coincidental on the git root's own directory name): it
// resolves the same way regardless of what the checkout's top-level folder is called.
const root = path.resolve(import.meta.dirname, "..");
const catalogPath = path.join(root, "extensions", "catalog.json");
const buildPropsPath = path.join(root, "Directory.Build.props");
const solutionFileName = "CoveExtensions.slnx";
const solutionPath = path.join(root, solutionFileName);
const errors = [];

function isLowerKebab(value) {
  return value === value.toLowerCase() && !value.includes(" ");
}

function readMsBuildProperties(filePath) {
  return fs.existsSync(filePath) ? parseMsBuildProperties(fs.readFileSync(filePath, "utf8")) : {};
}

// Returns both counts so the caller can tell "the solution declares no projects" from "the path
// attribute stopped being readable". The two are indistinguishable by the extracted set alone, and
// the second silently confirms every membership.
function readSolutionProjects(filePath) {
  const content = fs.readFileSync(filePath, "utf8");
  const elements = content.match(/<Project\b[^>]*>/g) ?? [];
  const paths = [];
  for (const element of elements) {
    const match = element.match(/\bPath\s*=\s*"([^"]*)"/);
    if (match) paths.push(match[1]);
  }
  return { elementCount: elements.length, paths };
}

// A solution authored on Windows and a catalog written with forward slashes describe the same file.
// Case is deliberately left alone: a case mismatch breaks the Linux build, so it must not compare
// equal on either platform.
function normalizeSeparators(value) {
  return value.replaceAll("\\", "/");
}

function validateVersionFloor(label, field, value, minimum) {
  if (!value) {
    errors.push(`${label}: ${field} is missing`);
    return;
  }

  const parsedValue = parseSemver(value);
  const parsedMinimum = parseSemver(minimum);
  if (parsedValue === null) {
    errors.push(`${label}: ${field} must be a semantic version, found ${value}`);
  } else if (parsedMinimum === null) {
    errors.push(
      `Directory.Build.props: CoveMinVersion must be a semantic version, found ${minimum}`,
    );
  } else if (compareSemver(parsedValue, parsedMinimum) < 0) {
    errors.push(`${label}: ${field} ${value} is below repo CoveMinVersion ${minimum}`);
  }
}

function validateExternalDependencies(extensionId, manifest) {
  if (manifest.externalDependencies == null) return;
  if (!Array.isArray(manifest.externalDependencies)) {
    errors.push(`${extensionId}: extension.json externalDependencies must be an array`);
    return;
  }

  for (const dependency of manifest.externalDependencies) {
    if (!dependency?.id) errors.push(`${extensionId}: external dependency missing id`);
    if (!dependency?.name) errors.push(`${extensionId}: external dependency missing name`);
    if (Object.hasOwn(dependency, "optional")) {
      errors.push(`${extensionId}: external dependency uses legacy optional; use required`);
    }
    if (Object.hasOwn(dependency, "settingsKey")) {
      errors.push(
        `${extensionId}: external dependency uses legacy settingsKey; use configurationKeys`,
      );
    }
    if (dependency.configurationKeys != null && !Array.isArray(dependency.configurationKeys)) {
      errors.push(`${extensionId}: external dependency configurationKeys must be an array`);
    }
  }
}

function validateSettings(extensionId, manifest) {
  if (manifest.settings == null) return;
  if (!Array.isArray(manifest.settings)) {
    errors.push(`${extensionId}: extension.json settings must be an array`);
    return;
  }

  for (const setting of manifest.settings) {
    if (!setting?.name) errors.push(`${extensionId}: setting missing name`);
    if (Object.hasOwn(setting, "key")) {
      errors.push(`${extensionId}: setting uses legacy key; use name`);
    }
    if (Object.hasOwn(setting, "label")) {
      errors.push(`${extensionId}: setting uses legacy label; use displayName`);
    }
    if (Object.hasOwn(setting, "defaultValue")) {
      errors.push(
        `${extensionId}: setting uses legacy defaultValue; remove it from extension.json`,
      );
    }
    if (Object.hasOwn(setting, "scope")) {
      errors.push(`${extensionId}: setting uses legacy scope; remove it from extension.json`);
    }
  }
}

const catalog = readJson(catalogPath);
const entries = Array.isArray(catalog.extensions) ? catalog.extensions : [];
const buildProps = readMsBuildProperties(buildPropsPath);
const coveMinVersion = buildProps.CoveMinVersion ?? null;

if (!catalog.schemaVersion) errors.push("extensions/catalog.json missing schemaVersion");
if (entries.length === 0) errors.push("extensions/catalog.json has no extensions");

// Counts the surviving floor comparisons so the success line can prove the check ran rather than
// merely exited 0. A repo declaring no CoveMinVersion runs no comparison at all, and that no-op is
// stated in the report line rather than left to look like a pass.
let floorComparisons = 0;

// The catalog's optional path fields, as consumed by .github/workflows/ci.yml or by this validator
// - every `matrix.extension.*` value there that names a location on disk and is not already covered by
// a check above (path, manifestPath and projectPath are), plus registryManifestPath, which this file
// reads for the floor comparison below. e2eProject is excluded deliberately: it is a Playwright project
// name, not a path. Each of these is optional to declare, so an entry declaring none is valid and only a
// declared one is required to exist.
const matrixPathFields = ["testProjectPath", "uiPath", "e2ePath", "registryManifestPath"];
let declaredPathChecks = 0;

// Counts every entry that declared a registry manifest, incremented before the file is read, and every
// row actually compared. The pair is what separates "no entry declares one" from "one was declared and
// carries no row for the current version" - states a single counter would render identical, and the
// second of which is legitimate: releasing.md requires the release asset before the registry pull
// request, so a version bumped ahead of its row is an ordinary mid-release state, not a defect.
let registrySubjects = 0;
let registryFloorComparisons = 0;

// Every C# project the catalog implies, as {id, field, value}, gathered across all entries so the
// solution is read once rather than per entry.
const impliedProjects = [];

const ids = new Set();
const tagPrefixes = new Set();
for (const entry of entries) {
  for (const field of ["name", "id", "path", "tagPrefix"]) {
    if (!entry[field])
      errors.push(`${entry.id ?? entry.name ?? "catalog entry"}: missing ${field}`);
  }

  if (entry.id && ids.has(entry.id)) errors.push(`${entry.id}: duplicate extension id`);
  if (entry.id) ids.add(entry.id);

  if (entry.tagPrefix && tagPrefixes.has(entry.tagPrefix))
    errors.push(`${entry.id}: duplicate tagPrefix ${entry.tagPrefix}`);
  if (entry.tagPrefix) tagPrefixes.add(entry.tagPrefix);
  if (entry.tagPrefix && !entry.tagPrefix.endsWith("/"))
    errors.push(`${entry.id}: tagPrefix must end with /`);

  const extensionDir = path.join(root, entry.path ?? "");
  // Fork adaptation #1: prefer the catalog entry's explicit manifestPath/projectPath when present
  // (Renamer's real layout nests both one level deeper under src/Renamer/), falling back to the
  // upstream's {path}/extension.json and {path}/{name}.csproj convention when the entry omits
  // them, so a flat-convention entry added later still validates unchanged.
  const manifestPath = entry.manifestPath
    ? path.join(root, entry.manifestPath)
    : path.join(extensionDir, "extension.json");
  const projectPath = entry.projectPath
    ? path.join(root, entry.projectPath)
    : path.join(extensionDir, `${entry.name}.csproj`);

  // Deliberately ahead of the short-circuits below: a mis-pointed CI path is worth reporting even
  // on an entry whose missing directory or manifest would otherwise `continue` straight past it.
  for (const field of matrixPathFields) {
    if (!entry[field]) continue;
    declaredPathChecks++;
    if (!fs.existsSync(path.join(root, entry[field]))) {
      errors.push(`${entry.id}: ${field} does not exist: ${entry[field]}`);
    }
  }

  // Same reasoning as the loop above: an entry that short-circuits below still declares projects the
  // C# gates would have to compile, and a solution gap is worth reporting alongside whatever else is
  // wrong with the entry.
  if (entry.projectPath) {
    impliedProjects.push({ id: entry.id, field: "projectPath", value: entry.projectPath });
  } else if (entry.path && entry.name) {
    impliedProjects.push({
      id: entry.id,
      field: "projectPath (by convention)",
      value: path.posix.join(entry.path, `${entry.name}.csproj`),
    });
  }
  if (entry.testProjectPath) {
    impliedProjects.push({ id: entry.id, field: "testProjectPath", value: entry.testProjectPath });
  }

  if (!fs.existsSync(extensionDir)) {
    errors.push(`${entry.id}: path does not exist: ${entry.path}`);
    continue;
  }
  if (!fs.existsSync(manifestPath)) {
    errors.push(`${entry.id}: missing extension.json at ${entry.manifestPath ?? entry.path}`);
    continue;
  }
  if (!fs.existsSync(projectPath)) {
    const conventionProject = `${entry.name}.csproj at ${entry.path}`;
    errors.push(`${entry.id}: missing project ${entry.projectPath ?? conventionProject}`);
  }

  const manifest = readJson(manifestPath);
  if (manifest.id !== entry.id)
    errors.push(`${entry.id}: catalog id does not match extension.json id ${manifest.id}`);
  if (!manifest.version) errors.push(`${entry.id}: extension.json missing version`);
  if (coveMinVersion) {
    validateVersionFloor(
      entry.id,
      "extension.json minCoveVersion",
      manifest.minCoveVersion,
      coveMinVersion,
    );
    floorComparisons++;
  }

  // Fork deviation #6. Reads the `manifest` object bound above rather than parsing it a second time,
  // and compares only the row describing the version that object currently declares.
  if (entry.registryManifestPath) {
    registrySubjects++;
    const registryPath = path.join(root, entry.registryManifestPath);
    // A declared path that is absent is already reported by the matrixPathFields loop above. Reading
    // it here would throw, where every other failure in this file is a pushed error.
    if (fs.existsSync(registryPath)) {
      const registry = readJson(registryPath);
      if (!Array.isArray(registry.versions)) {
        errors.push(
          `${entry.id}: registry manifest ${entry.registryManifestPath} has no versions[] array`,
        );
      } else {
        // Without this, "the row matching the current version" is not well defined, so the guard below
        // would silently pick whichever duplicate came first.
        const seenVersions = new Set();
        for (const row of registry.versions) {
          if (row?.version == null) continue;
          if (seenVersions.has(row.version)) {
            errors.push(
              `${entry.id}: registry manifest ${entry.registryManifestPath} declares versions[] row ${row.version} more than once, so the row describing the current version is ambiguous`,
            );
          }
          seenVersions.add(row.version);
        }

        const currentRow = registry.versions.find(
          (row) => row?.version != null && row.version === manifest.version,
        );
        // No matching row is not a defect: releasing.md requires the release asset before the registry
        // pull request, so a version bumped ahead of its row is an ordinary mid-release state. The
        // report line below is what keeps it from passing silently.
        if (currentRow != null) {
          if (!currentRow.minCoveVersion) {
            errors.push(
              `${entry.id}: registry manifest ${entry.registryManifestPath} versions[] row ${currentRow.version} declares no minCoveVersion, so its floor cannot be compared`,
            );
          } else if (currentRow.minCoveVersion !== manifest.minCoveVersion) {
            errors.push(
              `${entry.id}: registry manifest ${entry.registryManifestPath} versions[] row ${currentRow.version} declares minCoveVersion ${currentRow.minCoveVersion}, but extension.json declares ${manifest.minCoveVersion} — a raised floor reaches the registry by prepending a row for the release being cut. Do not edit an existing row — each one describes an immutable published artifact.`,
            );
          } else {
            registryFloorComparisons++;
          }
        }
      }
    }
  }
  if (!manifest.entryDll) errors.push(`${entry.id}: extension.json missing entryDll`);
  if (!manifest.url) errors.push(`${entry.id}: extension.json missing url`);
  if (!Array.isArray(manifest.categories) || manifest.categories.length === 0) {
    errors.push(`${entry.id}: extension.json missing categories`);
  } else {
    for (const category of manifest.categories) {
      if (!isLowerKebab(category))
        errors.push(`${entry.id}: category must be lowercase kebab-case: ${category}`);
    }
  }

  validateExternalDependencies(entry.id, manifest);
  validateSettings(entry.id, manifest);
}

let solutionMemberships = 0;
if (impliedProjects.length > 0) {
  if (!fs.existsSync(solutionPath)) {
    errors.push(
      `${solutionFileName} is missing, so ${impliedProjects.length} catalog-implied C# project(s) cannot be checked`,
    );
  } else {
    const { elementCount, paths } = readSolutionProjects(solutionPath);
    if (elementCount !== paths.length) {
      errors.push(
        `${solutionFileName}: found ${elementCount} project element(s) but read ${paths.length} path(s) from them`,
      );
    } else {
      const declared = new Set(paths.map(normalizeSeparators));
      for (const project of impliedProjects) {
        if (declared.has(normalizeSeparators(project.value))) {
          solutionMemberships++;
        } else {
          errors.push(
            `${project.id}: ${project.field} ${project.value} is not declared in ${solutionFileName}`,
          );
        }
      }
    }
  }
}

if (errors.length > 0) {
  for (const error of errors) console.error(`ERROR: ${error}`);
  process.exit(1);
}

// Counts, unconditionally - never a sentence per check. A zero is a number here, which is the whole
// point: a clause that changes its wording when a check has no subject makes "this ran and found
// nothing wrong" and "this never ran" two readings of the same line, and that collision is what let
// the self-comparing checks removed in deviation #2 stay invisible. Every counter above is
// incremented at the point its check examines a subject, so this line is what the run examined.
console.log(
  `Validated ${entries.length} extension catalog entries: ` +
    `${floorComparisons} minCoveVersion floor comparison(s), ` +
    `${declaredPathChecks} declared catalog path(s), ` +
    `${solutionMemberships} ${solutionFileName} membership(s), ` +
    `${registryFloorComparisons} registry row(s) compared across ` +
    `${registrySubjects} declared registry manifest(s).`,
);

// Fork of yourcove/multi-extension-repo-template's scripts/validate-extension-repo.mjs.
// Forked on: 2026-07-01
// Upstream diff base: https://github.com/yourcove/multi-extension-repo-template/blob/main/scripts/validate-extension-repo.mjs
//
// Five behavioral differences from upstream.
//
// 1. This fork reads the additive projectPath/manifestPath catalog fields (when present on a
// catalog entry) instead of unconditionally deriving {path}/{name}.csproj and {path}/extension.json
// by convention. This lets a real 3-project src/ subtree layout (e.g. Renamer's
// extensions/Renamer/src/{Renamer,Renamer.Ui}/) be described explicitly, while a future
// manifestOnly or flat-convention entry added WITHOUT these fields still validates correctly via
// the upstream convention-derived fallback path — the fork is additive, not a breaking
// replacement.
//
// 2. Upstream floor-checks the two package-version properties in Directory.Build.props against
// CoveMinVersion. This fork checks neither, because in this repo neither comparison has a subject:
// the SDK version is declared as $(CoveMinVersion), so comparing it to the floor asks whether a
// value is at least itself, and no companion property is declared for Cove.Core at all — it is
// never a PackageReference here, arriving transitively through Cove.Sdk. Deriving the version makes
// that drift unrepresentable, which is stronger than detecting it after the fact. The per-entry
// extension.json minCoveVersion comparison, which does have a real subject, survives.
//
// 3. Upstream's success line reports only how many catalog entries it walked. This fork reports
// what it actually examined: how many floor comparisons it ran and how many declared catalog paths
// it confirmed, or that neither had a subject. Exit 0 alone cannot distinguish a check that passed
// from one that never ran — which is exactly how the self-comparing checks removed in #2 stayed
// invisible.
//
// 4. This fork confirms that every optional catalog path field the CI build matrix consumes exists
// on disk (see matrixPathFields below). Upstream's catalog declares no such fields, so it has
// nothing to check; here a typo in one is otherwise caught only late and cryptically inside a
// matrix leg, as an `npm ci` in a directory that is not there or a dotnet restore several steps in.
//
// 5. This fork asserts that every C# project the catalog implies is declared in CoveExtensions.slnx.
// The formatting and analyzer gates take their entire subject list from that solution, so a project
// absent from it is not reported as unformatted or non-compliant — it is never compiled, and the
// gate reports success over a smaller set than the reader believes. That is a silent loss of
// coverage for every extension after the first, and the only kind of failure this repository treats
// as worse than a red gate. Detection rather than generation is deliberate: generating the solution
// from the catalog would reintroduce a checked-in generated artifact and its drift risk, and
// pointing the gates at a glob instead would change what actually gets compiled. Asserting
// membership changes neither, and costs one named error instead of a hole. The assertion runs one
// way only — the solution may hold projects the catalog does not describe, as shared libraries are.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";

// root is the parent of this file's own scripts/ directory — matching upstream's template
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

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8").replace(/^\uFEFF/, ""));
}

function isLowerKebab(value) {
  return value === value.toLowerCase() && !value.includes(" ");
}

function readMsBuildProperties(filePath) {
  if (!fs.existsSync(filePath)) return {};

  const props = {};
  const content = fs.readFileSync(filePath, "utf8");
  const pattern = /<([A-Za-z_][A-Za-z0-9_.-]*)(?:\s+[^>]*)?>([^<]*)<\/\1>/g;
  for (const match of content.matchAll(pattern)) {
    const [, name, rawValue] = match;
    const value = rawValue
      .trim()
      .replace(/\$\(([^)]+)\)/g, (_, propertyName) => props[propertyName] ?? `$(${propertyName})`);
    props[name] = value;
  }

  return props;
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

function parseVersion(value) {
  if (typeof value !== "string") return null;
  const match = value.match(/^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$/);
  if (!match) return null;
  return match.slice(1).map((part) => Number.parseInt(part, 10));
}

function compareVersions(left, right) {
  const leftParts = parseVersion(left);
  const rightParts = parseVersion(right);
  if (!leftParts || !rightParts) return null;

  for (let i = 0; i < 3; i++) {
    if (leftParts[i] !== rightParts[i]) return leftParts[i] - rightParts[i];
  }

  return 0;
}

function validateVersionFloor(label, field, value, minimum) {
  if (!value) {
    errors.push(`${label}: ${field} is missing`);
    return;
  }

  const comparison = compareVersions(value, minimum);
  if (comparison == null) {
    errors.push(`${label}: ${field} must be a semantic version, found ${value}`);
  } else if (comparison < 0) {
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
    if (Object.prototype.hasOwnProperty.call(dependency, "optional")) {
      errors.push(`${extensionId}: external dependency uses legacy optional; use required`);
    }
    if (Object.prototype.hasOwnProperty.call(dependency, "settingsKey")) {
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
    if (Object.prototype.hasOwnProperty.call(setting, "key")) {
      errors.push(`${extensionId}: setting uses legacy key; use name`);
    }
    if (Object.prototype.hasOwnProperty.call(setting, "label")) {
      errors.push(`${extensionId}: setting uses legacy label; use displayName`);
    }
    if (Object.prototype.hasOwnProperty.call(setting, "defaultValue")) {
      errors.push(
        `${extensionId}: setting uses legacy defaultValue; remove it from extension.json`,
      );
    }
    if (Object.prototype.hasOwnProperty.call(setting, "scope")) {
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

// The catalog's optional path fields, as consumed by .github/workflows/build.yml — every
// `matrix.extension.*` value there that names a location on disk and is not already covered by a
// check above (path, manifestPath and projectPath are). e2eProject is excluded deliberately: it is
// a Playwright project name, not a path. Each of these is optional, so an entry declaring none is
// valid; only a declared one is required to exist.
const matrixPathFields = ["testProjectPath", "uiPath", "e2ePath", "e2eNodeTestsPath"];
let declaredPathChecks = 0;

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
  // them, so a flat-convention or manifestOnly entry added later still validates unchanged.
  const manifestPath = entry.manifestPath
    ? path.join(root, entry.manifestPath)
    : path.join(extensionDir, "extension.json");
  const projectPath = entry.projectPath
    ? path.join(root, entry.projectPath)
    : path.join(extensionDir, `${entry.name}.csproj`);
  const isManifestOnly = entry.manifestOnly === true;

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
  } else if (!isManifestOnly && entry.path && entry.name) {
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
  if (!isManifestOnly && !fs.existsSync(projectPath)) {
    errors.push(
      `${entry.id}: missing project ${entry.projectPath ?? `${entry.name}.csproj at ${entry.path}`}`,
    );
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
  if (!isManifestOnly && !manifest.entryDll)
    errors.push(`${entry.id}: extension.json missing entryDll`);
  if (isManifestOnly && manifest.entryDll)
    errors.push(`${entry.id}: manifestOnly entry must not declare entryDll`);
  if (isManifestOnly && !["bundle", "scraper-pack"].includes(manifest.kind)) {
    errors.push(`${entry.id}: manifestOnly entries must use kind=bundle or kind=scraper-pack`);
  }
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

const floorReport = coveMinVersion
  ? `compared ${floorComparisons} extension.json minCoveVersion declaration(s) against CoveMinVersion ${coveMinVersion}`
  : "no CoveMinVersion declared in Directory.Build.props, so no floor comparison ran";
const pathReport = declaredPathChecks
  ? `confirmed ${declaredPathChecks} declared catalog path(s) exist`
  : "no entry declared an optional catalog path, so none were checked";
const solutionReport = impliedProjects.length
  ? `confirmed ${solutionMemberships} project membership(s) in ${solutionFileName}`
  : "no catalog entry implied a C# project, so no solution membership was checked";
console.log(
  `Validated ${entries.length} extension catalog entries (${floorReport}; ${pathReport}; ${solutionReport}).`,
);

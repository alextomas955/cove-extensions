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
// subject here — the SDK version IS $(CoveMinVersion), so the comparison asks whether a value is at
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
const WIRE_DOCUMENT_SUBPATH = "wire/openapi.json";
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

// Build output, dependencies and the docs site, none of which hold first-party project source. An
// extension placing a .csproj under one of these names is invisible to the reference rule below, so
// the summary names them rather than leaving the hole here where no run output shows it.
const referenceScanSkippedDirectories = new Set(["node_modules", "bin", "obj", ".git", "website"]);

// Directory.Build.props/.targets carry ProjectReference items that MSBuild injects into every project
// beneath them, which is the one place a reference can be added without editing any .csproj at all.
const referenceScanFileNames = new Set(["Directory.Build.props", "Directory.Build.targets"]);

// Walked from the repository root rather than read from the catalog, because a project taking the
// forbidden reference is by definition one the catalog does not declare.
// Dirent.isDirectory() is false for a symlink, so a project reached only through one is not walked.
// The summary says so; an unreadable directory is reported by name rather than thrown as a stack
// trace, because either way the rule has not read what is under it.
function collectProjectFiles(dir, collected = []) {
  let entries;
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch (cause) {
    errors.push(
      `${normalizeSeparators(path.relative(root, dir)) || "."}: could not be read, so no project file under it was checked for a ProjectReference (${cause.code}).`,
    );
    return collected;
  }

  for (const entry of entries) {
    if (entry.isDirectory()) {
      if (referenceScanSkippedDirectories.has(entry.name)) continue;
      collectProjectFiles(path.join(dir, entry.name), collected);
    } else if (
      entry.isFile() &&
      (entry.name.endsWith(".csproj") || referenceScanFileNames.has(entry.name))
    ) {
      collected.push(path.join(dir, entry.name));
    }
  }
  return collected;
}

// Resolved against the containing project's own directory, because that is how MSBuild reads a
// relative Include, and returned repo-relative so it can be compared against a catalog field. An
// Include resolving outside the root yields leading parent segments, which match no catalog value.
//
// An Include is an MSBuild item list, so one attribute may name several projects separated by
// semicolons, and XML admits either quote character as the delimiter. A segment naming a property or
// a wildcard is collected into unresolvable rather than resolved, because expanding one needs
// MSBuild's own evaluation - and this repo's own wiring writes Includes in that spelling, so a rule
// that dropped them would be silent over the shape most likely to carry the reference.
// Several project files here discuss ProjectReference wiring in prose, and the first one to quote
// the markup would otherwise fail a blocking gate on a comment. Returns null when a comment is
// opened and never closed, because nothing after that opener can be told from live markup; MSBuild
// rejects such a file outright, so the caller names it rather than judging half of it.
function xmlCommentSpans(text) {
  const spans = [];
  const opener = /<!--/g;
  let match;
  while ((match = opener.exec(text)) !== null) {
    const close = text.indexOf("-->", match.index + 4);
    if (close === -1) return null;
    spans.push([match.index, close + 3]);
    opener.lastIndex = close + 3;
  }
  return spans;
}

function readProjectReferences(projectFilePath, unresolvable, unparsable) {
  const content = fs.readFileSync(projectFilePath, "utf8");
  const commentSpans = xmlCommentSpans(content);
  if (commentSpans === null) {
    unparsable.push(normalizeSeparators(path.relative(root, projectFilePath)));
    return [];
  }
  const projectDir = path.dirname(projectFilePath);
  const references = [];
  const pattern = /<ProjectReference\b[^>]*?\bInclude\s*=\s*(["'])(.*?)\1/g;
  for (const match of content.matchAll(pattern)) {
    if (commentSpans.some(([start, end]) => match.index >= start && match.index < end)) continue;
    for (const raw of match[2].split(";")) {
      const include = raw.trim();
      if (include === "") continue;
      if (include.includes("$(") || include.includes("*")) {
        unresolvable.push(
          `${normalizeSeparators(path.relative(root, projectFilePath))}: ${include}`,
        );
        continue;
      }

      const resolved = path.resolve(projectDir, normalizeSeparators(include));
      references.push(normalizeSeparators(path.relative(root, resolved)));
    }
  }
  return references;
}

function parseVersion(value) {
  if (typeof value !== "string") return null;
  const match = /^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$/.exec(value);
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

// The catalog's optional path fields, as consumed by .github/workflows/build.yml or by this validator
// — every `matrix.extension.*` value there that names a location on disk and is not already covered by
// a check above (path, manifestPath and projectPath are), plus registryManifestPath, which this file
// reads for the floor comparison below. e2eProject is excluded deliberately: it is a Playwright project
// name, not a path. Each of these is optional to DECLARE, so an entry declaring none is valid and only a
// declared one is required to exist.
//
// The two test fields name two projects, split on whether a test needs a Cove source checkout to
// compile. Only the Cove one presupposes the other, which is checked as a pairing below.
const matrixPathFields = [
  "testProjectPath",
  "coveTestProjectPath",
  "uiPath",
  "e2ePath",
  "e2eNodeTestsPath",
  "registryManifestPath",
];
let declaredPathChecks = 0;

// Counts every entry that DECLARED a registry manifest, incremented before the file is read, and every
// row actually compared. The pair is what separates "no entry declares one" from "one was declared and
// carries no row for the current version" — states a single counter would render identical, and the
// second of which is legitimate: releasing.md requires the release asset before the registry pull
// request, so a version bumped ahead of its row is an ordinary mid-release state, not a defect.
let registrySubjects = 0;
let registryFloorComparisons = 0;

// Every C# project the catalog implies, as {id, field, value}, gathered across all entries so the
// solution is read once rather than per entry.
const impliedProjects = [];

// The subset that requires a Cove source checkout, gathered separately so the repository is walked
// once, and only when there is something to compare against.
const coveTestProjects = [];

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

  // A manifestOnly entry declares that it ships no built assembly, while uiPath tells the CI build
  // several times over to install, generate, type-check and bundle a frontend — for an entry with
  // nothing to load it. The pairing is incoherent in one direction only: a UI on an assembly-bearing
  // entry is ordinary. Refusing it here, where it is a pure catalog fact, is what keeps those build
  // conditions from each needing their own copy of this guard. Placed with the entry-level checks
  // rather than beside the manifest-reading manifestOnly refusals below, because the short-circuits
  // at the end of this block `continue` past those for an entry whose directory or manifest is
  // missing — which is exactly the malformed entry most likely to carry this defect.
  if (isManifestOnly && entry.uiPath) {
    errors.push(
      `${entry.id}: declares both manifestOnly and uiPath, so CI would build and bundle a frontend for an entry that ships no assembly to load it`,
    );
  }

  // Each field can be individually well-formed while the pairing names a topology that does not
  // build: the Cove-tier test project reaches the shared TestSupport helpers through a
  // ProjectReference onto the pure one.
  if (entry.coveTestProjectPath && !entry.testProjectPath) {
    errors.push(
      `${entry.id}: declares coveTestProjectPath without a testProjectPath — the Cove test project reaches the shared TestSupport helpers through a ProjectReference onto the pure one`,
    );
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
  if (entry.coveTestProjectPath) {
    impliedProjects.push({
      id: entry.id,
      field: "coveTestProjectPath",
      value: entry.coveTestProjectPath,
    });
    coveTestProjects.push({ id: entry.id, value: entry.coveTestProjectPath });
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
    const conventionProject = `${entry.name}.csproj at ${entry.path}`;
    errors.push(`${entry.id}: missing project ${entry.projectPath ?? conventionProject}`);
  }

  // An entry with a UI must carry an emitted wire document: a hand-written TypeScript wire type
  // type-checks while reading undefined at runtime, so an extension that gains a UI without gaining
  // the derived document loses that check silently.
  if (entry.uiPath) {
    // POSIX-spelled once, as the catalog spells it: path.join takes it as the native path, and the
    // message reads the same whichever runner produced it.
    const wireDocument = entry.path + "/" + WIRE_DOCUMENT_SUBPATH;
    if (!fs.existsSync(path.join(root, wireDocument))) {
      errors.push(entry.id + ": declares uiPath, so " + wireDocument + " must exist");
    }
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

  // Fork deviation #7. Reads the `manifest` object bound above rather than parsing it a second time,
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
        // No matching row is NOT a defect: releasing.md requires the release asset before the registry
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

// SkipWithoutCoveSource.targets states the condition this asserts: $(SolutionPath) separates a
// solution build from a direct one, and it does not separate an entry project from one reached
// through a ProjectReference. The invariant holds today only because nothing takes that reference.
let projectFilesScanned = 0;
const unresolvableIncludes = [];
const unparsableProjectFiles = [];
if (coveTestProjects.length > 0) {
  const declaredCoveTestProjects = new Map(
    coveTestProjects.map((project) => [normalizeSeparators(project.value), project]),
  );
  for (const projectFile of collectProjectFiles(root)) {
    projectFilesScanned++;
    const referencingProject = normalizeSeparators(path.relative(root, projectFile));
    for (const reference of readProjectReferences(
      projectFile,
      unresolvableIncludes,
      unparsableProjectFiles,
    )) {
      const target = declaredCoveTestProjects.get(reference);
      if (!target) continue;
      errors.push(
        `${target.id}: ${referencingProject} declares a ProjectReference onto coveTestProjectPath ${target.value}, which requires a Cove source checkout. The solution build's skip for that project is scoped by $(SolutionPath), which does not separate a referenced project from an entry project, so ${referencingProject} would take the skip and then fail to resolve an output that was never produced.`,
      );
    }
  }
}

if (errors.length > 0) {
  for (const error of errors) console.error(`ERROR: ${error}`);
  process.exit(1);
}

// Counts, unconditionally — never a sentence per check. A zero is a number here, which is the whole
// point: a clause that changes its wording when a check has no subject makes "this ran and found
// nothing wrong" and "this never ran" two readings of the same line, and that collision is what let
// the self-comparing checks removed in deviation #2 stay invisible. Every counter above is
// incremented at the point its check examines a subject, so this line is what the run examined.
for (const unjudged of unresolvableIncludes) {
  console.log(
    `NOTICE: ${unjudged} - Include not resolvable without MSBuild evaluation, so the reference rule did not judge it.`,
  );
}

for (const unparsable of unparsableProjectFiles) {
  console.log(
    `NOTICE: ${unparsable} - an XML comment is opened and never closed, so the reference rule did not judge this file.`,
  );
}

console.log(
  `Validated ${entries.length} extension catalog entries: ` +
    `${floorComparisons} minCoveVersion floor comparison(s), ` +
    `${declaredPathChecks} declared catalog path(s), ` +
    `${solutionMemberships} ${solutionFileName} membership(s), ` +
    `${registryFloorComparisons} registry row(s) compared across ` +
    `${registrySubjects} declared registry manifest(s), ` +
    `${projectFilesScanned} project file(s) scanned for ProjectReference items onto ` +
    `${coveTestProjects.length} declared Cove test project(s), ` +
    `${unresolvableIncludes.length} Include(s) left unjudged, ` +
    `${unparsableProjectFiles.length} project file(s) left unjudged for an unclosed comment; ` +
    `directories named ${[...referenceScanSkippedDirectories].join(", ")} and all symlinked ` +
    `directories were not walked.`,
);

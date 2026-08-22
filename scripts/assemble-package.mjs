// Catalog-driven package assembler: copies exactly the files a catalog entry's `artifacts` array
// declares into an empty package directory, stamping the release version onto the manifest. Because
// the set is declared rather than discovered, whatever else the build emits alongside them (debug
// symbols, XML docs) cannot reach the package, and a reviewer reads catalog.json instead of listing a
// build output.
//
// This script never deletes. A package directory that is not empty is refused, which needs no notion
// of what would have been safe to destroy; the one real delete stays with the caller that owns the
// install location.
//
// Three callers share it: the CI build job, the local dev deploy, and the E2E harness, which imports
// assemblePackage in-process so the package it installs is the package a release ships.
//
// `import.meta.main` rather than a hand-rolled comparison against process.argv[1]: Node realpaths the
// entry, so the hand-rolled form answers "no" when the script is reached through a junction and then
// assembles nothing while exiting 0. The aliased-invocation cases in the test file pin this.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";

// import.meta.dirname, never a filesystem path read off a module URL's path component: on Windows
// that yields a leading-slash form which resolves to a doubled drive prefix.
const scriptRoot = path.resolve(import.meta.dirname, "..");

const BACKSLASH = String.fromCodePoint(92);
const QUOTE = String.fromCodePoint(34);

// Absolute-path markers are constructed from parts rather than written as bare literals so a
// downstream scan of this script's own source does not mistake the markers for a real leak.
//
// A Windows path inside json arrives escaped, so the text these markers read is not the text the
// build wrote: every separator is doubled, and a network share's two leading backslashes arrive as
// four. That escaping is what forces the two Windows classes apart, and it is why one anchor differs
// from the other:
//
//   - The drive marker accepts either separator, behind a word boundary. The boundary is what keeps a
//     url out: a multi-letter scheme has no word boundary before its last letter, so `https:` is not
//     a hit while a drive letter following a quote or a space is. A one-letter scheme would be a hit,
//     and is spelled exactly like a drive letter — there is nothing left to tell them apart by.
//   - The share marker instead requires its backslash run to BEGIN a value. Doubled backslashes in
//     the middle of a value are json escaping one separator, which an escaped drive path is full of,
//     so a share marker without that anchor matches every escaped drive path and stops being a
//     separate class at all.
const WINDOWS_DRIVE_ROOT = new RegExp(BACKSLASH + "b[A-Za-z]:[/" + BACKSLASH + BACKSLASH + "]");
const UNC_SHARE_ROOT = new RegExp(
  "(?<![^" + QUOTE + BACKSLASH + "s])" + BACKSLASH + BACKSLASH + "{2,}[A-Za-z0-9_.-]",
);
const UNIX_HOME_PREFIXES = ["/" + "home" + "/", "/" + "Users" + "/"];

// Each class is labelled so a refusal says which one fired. Two markers rather than one widened
// marker, because a later narrowing of either must be visible as a narrowing of that class alone —
// and a message that named neither would hide it.
const ABSOLUTE_PATH_MARKERS = [
  { label: "drive root", hits: (line) => WINDOWS_DRIVE_ROOT.test(line) },
  { label: "network share", hits: (line) => UNC_SHARE_ROOT.test(line) },
  {
    label: "unix home",
    hits: (line) => UNIX_HOME_PREFIXES.some((prefix) => line.includes(prefix)),
  },
];

// A declared artifact name is a bare filename that lands at the package root, so an absolute path,
// a `..` segment or any separator would send the copy writing outside the package directory.
// Rejected on shape before any source is read, since a path that escapes cannot be made safe by
// happening to resolve to something that exists.
const PATH_ESCAPE_PREFIX = new RegExp("^([A-Za-z]:|[/" + BACKSLASH + BACKSLASH + "])");
const PATH_SEPARATORS = new RegExp("[/" + BACKSLASH + BACKSLASH + "]");

const MANIFEST_BY_CONVENTION = "extension.json";

// The only names the repository root may supply: files a repository carries once, at its root, by
// convention, so a package's copy and the repository's copy are the same document. Every other name
// belongs to one extension, and a repository-root file matching it is a different document under the
// same name — which is why the root is offered for this list rather than for anything that happens to
// be there. Generic by construction: a name specific to any extension in this repository does not
// belong here, or the packer stops being catalog-driven.
const REPO_ROOT_FALLBACK_NAMES = new Set([
  "LICENSE",
  "LICENSE.md",
  "LICENSE.txt",
  "LICENCE",
  "LICENCE.md",
  "LICENCE.txt",
  "COPYING",
  "COPYING.LESSER",
  "NOTICE",
  "NOTICE.md",
  "NOTICE.txt",
]);

// The manifest fields whose value the host resolves as a file inside the installed package, in the
// order their failures are reported so identical input yields an identical message sequence. Each is
// optional; only a field the manifest actually carries makes a claim.
const MANIFEST_FILE_FIELDS = ["entryDll", "jsBundle", "cssBundle"];

function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8").replace(/^\uFEFF/, ""));
}

function resolveEntry(catalog, idOrName) {
  return catalog.extensions.find((entry) => entry.id === idOrName || entry.name === idOrName);
}

function checkArtifactName(name, failures) {
  if (typeof name !== "string" || name === "") {
    failures.push(
      "INVALID: artifacts entry must be a non-empty string, found: " + JSON.stringify(name),
    );
    return false;
  }

  const segments = name.split(PATH_SEPARATORS);
  if (
    PATH_ESCAPE_PREFIX.test(name) ||
    segments.length > 1 ||
    segments.includes("..") ||
    name === "."
  ) {
    failures.push(
      "ESCAPE: artifacts entry must be a bare filename with no path separator, found: " + name,
    );
    return false;
  }

  return true;
}

// Scans the text that is about to be WRITTEN, not the text on disk: the manifest's outgoing copy
// carries a caller-supplied version the source file does not, and the refusal is about package
// contents rather than about build output.
function checkNoAbsolutePath(name, text, failures) {
  text.split(/\r?\n/).forEach((line, index) => {
    // The first matching class names the line: a line is one kind of absolute path, and reporting a
    // second class for it would say nothing more about what the caller has to change.
    const marker = ABSOLUTE_PATH_MARKERS.find((candidate) => candidate.hits(line));
    if (marker) {
      failures.push(
        "LEAK: absolute path (" +
          marker.label +
          ") found in shipped json: " +
          name +
          ":" +
          (index + 1) +
          ": " +
          line.trim(),
      );
    }
  });
}

// A declaration can name only files that exist and still describe a package the host refuses to load:
// the catalog and the manifest are two hand-edited files that have to agree, and nothing until now
// made them. The strip-verification gate asserted these same two properties against the finished
// folder; asserting them against the declaration on the way in makes the unloadable package
// unrepresentable instead of detected, and costs no second read — the manifest is already parsed here
// to stamp the version.
//
// Failures are pushed, never returned, so one run still reports everything wrong with a declaration.
function checkDeclarationIsLoadable(sourceManifest, manifestName, names, failures) {
  if (!names.includes(manifestName)) {
    failures.push(
      "UNLOADABLE: artifacts does not declare " +
        manifestName +
        " — a package with no load manifest cannot be installed.",
    );
  }

  for (const field of MANIFEST_FILE_FIELDS) {
    const value = sourceManifest[field];
    if (typeof value !== "string" || value === "") continue;
    if (!names.includes(value)) {
      failures.push(
        "UNLOADABLE: the source manifest's " +
          field +
          " names " +
          value +
          ", which artifacts does not declare.",
      );
    }
  }
}

/**
 * Assembles a catalog entry's declared package into `packageDir`, which must be absent or empty.
 *
 * Nothing is written at all unless every declared artifact resolved, every shipped json passed the
 * absolute-path refusal, and the package directory was empty. Nothing is ever deleted, and nothing
 * outside `packageDir` is written: every write this function makes names a file directly inside it.
 *
 * Past that point the guarantee is narrower. A failed write leaves the package directory INCOMPLETE
 * and is reported as a named `WRITE:` entry rather than thrown, so a caller reading a non-zero result
 * as "nothing shipped" is right about the rest of its tree and wrong about the package directory.
 *
 * @param {object} opts
 * @param {string} opts.root - the repo root holding `extensions/catalog.json`.
 * @param {string} opts.publishDir - build output the declared names are searched in first.
 * @param {string} opts.packageDir - created if absent, refused if not empty.
 * @param {string} opts.idOrName - the catalog entry's `id` or `name`.
 * @param {string} opts.version - written into the packaged manifest verbatim.
 * @returns {{ ok: boolean, failures: string[], copied: Array<{ name: string, source: string, root: string }> }}
 */
export function assemblePackage({ root, publishDir, packageDir, idOrName, version }) {
  const failures = [];
  const copied = [];
  const done = () => ({ ok: failures.length === 0, failures, copied });

  const absoluteRoot = path.resolve(root);
  const absolutePublishDir = path.resolve(publishDir);
  const absolutePackageDir = path.resolve(packageDir);

  const catalog = readJson(path.join(absoluteRoot, "extensions", "catalog.json"));
  const entry = resolveEntry(catalog, idOrName);
  if (!entry) {
    failures.push("INVALID: no catalog entry matches id/name: " + idOrName);
    return done();
  }

  if (typeof version !== "string" || version === "") {
    failures.push("INVALID: version must be a non-empty string, found: " + JSON.stringify(version));
  }

  // The declared set is the only source of what ships. An entry that declares nothing, or declares
  // an empty array, is a hard failure rather than a run that copies nothing and exits 0 — there is
  // no narrower field to fall back to and no set to infer.
  const declared = entry.artifacts;
  if (declared == null) {
    failures.push(
      "MISSING: catalog entry " +
        entry.id +
        " declares no artifacts array — the shipped file set must be declared.",
    );
    return done();
  }
  if (!Array.isArray(declared) || declared.length === 0) {
    failures.push(
      "INVALID: catalog entry " +
        entry.id +
        " declares an empty artifacts array — an assemble that copies nothing inspected nothing.",
    );
    return done();
  }

  const seen = new Set();
  const names = [];
  for (const name of declared) {
    if (!checkArtifactName(name, failures)) continue;
    if (seen.has(name)) {
      failures.push("DUPLICATE: artifacts declares " + name + " more than once.");
      continue;
    }
    seen.add(name);
    names.push(name);
  }

  // Shape and declaration failures are returned before any source is read and before the package
  // directory is touched, so a malformed declaration cannot destroy an earlier good package.
  if (failures.length > 0) return done();

  // The one thing this script asks of a package directory, and the whole of what replaced the delete.
  // A directory holding anything at all is refused: a leftover from an earlier run would ship
  // alongside the declared set, and a directory holding something else is a caller pointing this at a
  // location it means to keep. Neither can be told apart from the other by inspection, and neither
  // needs to be — the answer to both is the same, and it is not to delete.
  //
  // Cheap by design. A single readdir replaces a canonical-key containment check over a protected-path
  // set, which existed only to bound what the delete could reach. There is no delete to bound.
  if (fs.existsSync(absolutePackageDir) && fs.readdirSync(absolutePackageDir).length > 0) {
    failures.push(
      "INVALID: package directory is not empty: " +
        absolutePackageDir +
        " — this packer never deletes, so it must be handed an absent or empty directory. " +
        "Clear it in the caller that owns it.",
    );
    return done();
  }

  // The packaged manifest is sourced from the entry's declared manifestPath rather than from the
  // publish output, because the publish output's copy is a build side effect: the manifest the host
  // reads at install time must be the one under source control, stamped with this release.
  const manifestSource = entry.manifestPath
    ? path.join(absoluteRoot, entry.manifestPath)
    : path.join(absoluteRoot, entry.path, MANIFEST_BY_CONVENTION);
  const manifestName = path.basename(manifestSource);

  let sourceManifest = null;
  if (fs.existsSync(manifestSource)) {
    try {
      sourceManifest = readJson(manifestSource);
    } catch (error) {
      failures.push(
        "INVALID: source manifest " +
          path.relative(absoluteRoot, manifestSource) +
          " is not parseable json: " +
          error.message,
      );
      return done();
    }
  } else {
    failures.push(
      "MISSING: source manifest is absent: " + path.relative(absoluteRoot, manifestSource),
    );
    return done();
  }

  checkDeclarationIsLoadable(sourceManifest, manifestName, names, failures);

  const uiBundleDir = entry.uiPath ? path.join(absoluteRoot, entry.uiPath, "dist") : null;

  // Ordered source search. The first two rules are exact — a declared name that matches one of them
  // is resolved from that root or not at all — and the remaining three are tried in order, so
  // precedence between roots is stated rather than left to whichever happens to hold the file.
  //
  // The ui-bundle rule covers BOTH bundle fields, because both are output of the same UI build and
  // neither is ever produced by the dotnet publish. Matching only `jsBundle` sent a declared
  // `cssBundle` down the publish/extension/repo-root search, where it cannot exist, so declaring one
  // failed as MISSING however correctly it had been built.
  // The roots a declared artifact may come from, in precedence order. The manifest and the UI
  // bundle each resolve from exactly one place; everything else is searched.
  function candidateSourcesFor(name) {
    if (name === manifestName) {
      return [{ source: manifestSource, root: "manifest" }];
    }
    if (uiBundleDir && (sourceManifest.jsBundle === name || sourceManifest.cssBundle === name)) {
      return [{ source: path.join(uiBundleDir, name), root: "ui-bundle" }];
    }
    return [
      { source: path.join(absolutePublishDir, name), root: "publish" },
      { source: path.join(absoluteRoot, entry.path, name), root: "extension" },
      { source: path.join(absoluteRoot, name), root: "repo-root", repoLevelOnly: true },
    ];
  }

  function resolveArtifactSource(name) {
    const searched = candidateSourcesFor(name);
    for (const candidate of searched) {
      // Skipped rather than dropped from `searched`: a root that was not offered is still a root the
      // caller has to know was considered, so the MISSING message stays as wide as the search.
      if (candidate.repoLevelOnly && !REPO_ROOT_FALLBACK_NAMES.has(name)) continue;
      if (fs.existsSync(candidate.source)) return { ...candidate, searched };
    }
    return { source: null, root: null, searched };
  }

  const staged = [];
  for (const name of names) {
    const { source, root: sourceRoot, searched } = resolveArtifactSource(name);
    if (source == null) {
      const roots = searched.map(
        (candidate) => path.relative(absoluteRoot, candidate.source) || candidate.source,
      );
      failures.push(
        "MISSING: declared artifact " +
          name +
          " was not found in any source root: " +
          roots.join(", "),
      );
      continue;
    }

    // The version is assigned as given: no semver parse, no coercion, so a placeholder such as a
    // triple zero survives into the package exactly as the caller spelled it.
    const text =
      name === manifestName ? JSON.stringify({ ...sourceManifest, version }, null, 2) + "\n" : null;
    staged.push({ name, source, root: sourceRoot, text });
  }

  for (const item of staged) {
    if (!item.name.endsWith(".json")) continue;
    checkNoAbsolutePath(item.name, item.text ?? fs.readFileSync(item.source, "utf8"), failures);
  }

  if (failures.length > 0) return done();

  // Created only here, once everything that could refuse has passed, so a failed run leaves no empty
  // directory behind for a caller to mistake for a package.
  fs.mkdirSync(absolutePackageDir, { recursive: true });

  for (const item of staged) {
    const destination = path.join(absolutePackageDir, item.name);
    try {
      if (item.text == null) {
        fs.copyFileSync(item.source, destination);
      } else {
        fs.writeFileSync(destination, item.text);
      }
    } catch (error) {
      // A source resolves on an existence check but is written here, so this is reachable — a
      // directory bearing a declared name, a locked destination, a full disk. The loop stops rather
      // than carrying on: a run that failed one write and wrote the rest would print a count for a
      // package that is not there, which is the shape the count exists to prevent.
      failures.push(
        "WRITE: " + item.name + " could not be written to " + destination + ": " + error.message,
      );
      break;
    }
    // Recorded at the point of the write, so the reported count is what landed rather than what was
    // declared.
    copied.push({
      name: item.name,
      source: path.relative(absoluteRoot, item.source),
      root: item.root,
    });
  }

  return done();
}

const REQUIRED_FLAGS = ["--publish-dir", "--package-dir", "--extension", "--version"];
// --root exists so the CLI leg can be exercised against a fixture tree rather than only against this
// checkout, which is the same reason assemblePackage takes `root` instead of closing over the
// constant above. It is the one flag with a default, because the answer for every real caller is the
// checkout the script itself lives in.
const ALL_FLAGS = new Set([...REQUIRED_FLAGS, "--root"]);

function usage() {
  console.error(
    "Usage: node scripts/assemble-package.mjs " +
      REQUIRED_FLAGS.map((flag) => flag + " <value>").join(" ") +
      " [--root <dir>]",
  );
}

function main(argv) {
  const options = new Map();
  for (let i = 0; i < argv.length; i += 2) {
    if (!ALL_FLAGS.has(argv[i]) || argv[i + 1] == null || options.has(argv[i])) {
      usage();
      return 1;
    }
    options.set(argv[i], argv[i + 1]);
  }

  // Every other flag is required rather than defaulted, and a flag supplied twice is refused above:
  // both otherwise let a run exit 0 having assembled from an argument the caller did not choose — one
  // they forgot, one they did not mean to win.
  if (REQUIRED_FLAGS.some((flag) => !options.get(flag))) {
    usage();
    return 1;
  }

  const root = options.get("--root") ? path.resolve(options.get("--root")) : scriptRoot;
  const result = assemblePackage({
    root,
    publishDir: path.resolve(options.get("--publish-dir")),
    packageDir: path.resolve(options.get("--package-dir")),
    idOrName: options.get("--extension"),
    version: options.get("--version"),
  });

  if (!result.ok) {
    for (const failure of result.failures) console.error(failure);
    console.error("Assemble FAILED — nothing was written.");
    return 1;
  }

  console.log(
    "Assembled " +
      options.get("--extension") +
      " " +
      options.get("--version") +
      " into " +
      path.relative(root, path.resolve(options.get("--package-dir"))) +
      " — " +
      result.copied.length +
      " file(s):",
  );
  for (const file of result.copied)
    console.log("  " + file.name + "  <- " + file.root + ": " + file.source);
  return 0;
}

/**
 * Whether this module is the process entry point, for the one runtime that cannot answer.
 *
 * @remarks
 * Both sides are realpathed, which is the whole reason this is not a plain `===` on the two strings:
 * Node realpaths the module URL and leaves process.argv[1] as the caller spelled it, so an invocation
 * through a junction or a symlink — the shape this repository's documented worktree workflow uses —
 * compares unequal, and the refusal below would then not fire on exactly the runtime and exactly the
 * invocation that need it. False on any answer it cannot establish: a wrong "yes" would break an
 * importer, where a wrong "no" only loses a diagnostic on a runtime this repository does not pin.
 */
function invokedAsScript() {
  const entry = process.argv[1];
  if (typeof entry !== "string" || entry === "") return false;
  const canonical = (value) => {
    let resolved = path.resolve(value);
    try {
      resolved = fs.realpathSync.native(resolved);
    } catch {
      // Left as resolved: a path that cannot be realpathed is one that does not exist, and comparing
      // the resolved form is no weaker than not comparing at all.
    }
    return process.platform === "win32" ? resolved.toLowerCase() : resolved;
  };
  return canonical(entry) === canonical(import.meta.filename);
}

// `import.meta.main` is a boolean from Node 22.18 onward and `undefined` before it, so a bare
// `if (import.meta.main)` takes the not-main branch on an older runtime: run as a CLI, this script
// would then print nothing and exit 0 — the same silent success the two-file split existed to prevent,
// arriving through the runtime instead of through a guard. The absent feature is refused BY NAME.
//
// Scoped to the CLI on purpose: assemblePackage works fine on an older Node, and refusing at import
// time would break the E2E harness and this file's own tests for a feature only the entry guard needs.
if (typeof import.meta.main !== "boolean") {
  if (invokedAsScript()) {
    console.error(
      `assemble-package: this Node (${process.version}) does not implement import.meta.main, so this script cannot tell it was run rather than imported and would assemble nothing while exiting 0. Node 22.18 or newer is required to run it.`,
    );
    process.exit(1);
  }
} else if (import.meta.main) {
  process.exit(main(process.argv.slice(2)));
}

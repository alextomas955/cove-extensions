// Shared, catalog-driven package assembler: copies exactly the files a catalog entry's `artifacts`
// array declares into a clean package directory, stamping the release version onto the manifest on
// the way through. The declaration is the whole answer to "what ships" — a reviewer reads
// extensions/catalog.json rather than running a build and listing a directory.
//
// Because the set is declared rather than discovered, anything the build happens to emit alongside
// the shipped files (debug symbols, XML documentation) cannot reach the package: it is not named, so
// it is not copied. The build is left free to keep producing it.
//
// Three callers share this one script: the CI build job, the local dev deploy, and the E2E harness,
// which imports the core below in-process so the package it installs is the package a release ships.
//
// This is the core; scripts/assemble-package.mjs is the command-line entry point and its whole body is
// the call. The two are split so that nothing decides whether the entry point runs: a condition there
// can be wrong in the one way that leaves no trace — an invocation reached through an alias of the
// script's own path assembling nothing and exiting 0, which every caller reads as success. Two files,
// still one package script.
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
const REPO_ROOT_FALLBACK_NAMES = [
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
];

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

// Returns a key for comparing two paths for identity, never a path to open. One directory has many
// spellings — a Windows drive letter is case-insensitive to the filesystem and case-sensitive to
// `===`, and a junction or symlink reaches the same directory under a different name — so a refusal
// that compares resolve() output as raw text is defeated by a caller who merely spells its argument
// differently, which is how a green run deleted a source tree here.
//
// The walk up to the deepest existing ancestor is what makes this usable on a package directory,
// which legitimately does not exist yet on a first run: realpath throws on a path that is not there.
// A realpath that fails on an ancestor that does exist falls back to the resolved form — the same
// text the previous guard compared, so the key is never weaker than what it replaced.
function canonicalPathKey(candidate) {
  const resolved = path.resolve(candidate);

  let existing = resolved;
  const missingSegments = [];
  while (!fs.existsSync(existing)) {
    const parent = path.dirname(existing);
    if (parent === existing) break;
    missingSegments.unshift(path.basename(existing));
    existing = parent;
  }

  let key;
  try {
    key = fs.realpathSync.native(existing);
  } catch {
    key = existing;
  }
  if (missingSegments.length > 0) key = path.join(key, ...missingSegments);

  return process.platform === "win32" ? key.toLowerCase() : key;
}

function isSameOrContains(candidateKey, protectedKey) {
  const prefix = candidateKey.endsWith(path.sep) ? candidateKey : candidateKey + path.sep;
  return candidateKey === protectedKey || protectedKey.startsWith(prefix);
}

// The set a package directory may neither be nor contain, labelled so a refusal names what the run
// would have destroyed. The catalog, the entry's source directory, its manifest and its UI directory
// are here because the entry declares them as source: a package directory containing any of them is
// pointed at source, whatever its relationship to the repo root happens to be.
//
// Deliberately not "anything under the repo root": two of the three real callers write inside the
// checkout by construction (CI's artifacts/<Name>, the dev deploy's <ext>/artifacts/package), so a
// containment rule in that direction refuses the callers this script exists to serve.
function protectedPaths(absoluteRoot, absolutePublishDir, entry, manifestSource) {
  const declared = [
    { label: "the repo root", target: absoluteRoot },
    { label: "the publish directory", target: absolutePublishDir },
    { label: "the catalog", target: path.join(absoluteRoot, "extensions", "catalog.json") },
    { label: "the extension source directory", target: path.join(absoluteRoot, entry.path) },
    { label: "the source manifest", target: manifestSource },
  ];
  if (entry.uiPath)
    declared.push({ label: "the UI directory", target: path.join(absoluteRoot, entry.uiPath) });

  return declared.map((item) => ({ ...item, key: canonicalPathKey(item.target) }));
}

// Empties the package directory's contents without removing the directory itself: a caller may point
// this at a live install location, where the directory's own existence and identity is what the host
// holds open. The enumeration exists solely to clear leftovers from an earlier run — nothing reads
// the package back afterwards to decide or confirm what shipped.
//
// Flat files only, and never a tree. Every declared artifact is a bare filename that lands at the
// package root — checkArtifactName refuses anything else before a source is read — so this packer
// cannot have written a subdirectory or a link here, and one found is always someone else's data.
// Treating it as a leftover to delete is what turned a path-math slip into a wiped source tree, so
// the refusal is a second defense that holds even if the packageDir guard is wrong again: the blast
// radius of a wrong answer is capped at the flat files in one directory.
//
// Nothing is removed unless the whole directory passed, so a refusal cannot take a sibling with it.
function emptyPackageDir(packageDir, failures) {
  if (!fs.existsSync(packageDir)) {
    fs.mkdirSync(packageDir, { recursive: true });
    return true;
  }

  const leftovers = fs.readdirSync(packageDir, { withFileTypes: true });
  const foreign = leftovers.find((leftover) => !leftover.isFile());
  if (foreign) {
    failures.push(
      "INVALID: package directory holds " +
        foreign.name +
        ", which is not a regular file and cannot be " +
        "something this packer wrote — refusing to empty " +
        packageDir,
    );
    return false;
  }

  // force tolerates a file that vanished between the enumeration and the unlink; recursive is absent
  // deliberately, and the case above is why it can be.
  for (const leftover of leftovers)
    fs.rmSync(path.join(packageDir, leftover.name), { force: true });
  return true;
}

/**
 * Assembles a catalog entry's declared package into `packageDir`.
 *
 * Nothing is written at all unless every declared artifact resolved, every shipped json passed the
 * absolute-path refusal, and the package directory held nothing but regular files.
 *
 * Past that point the guarantee is narrower, and the difference is what a caller has to act on. If a
 * write fails, the package directory is left INCOMPLETE — already emptied of the previous run, and
 * holding only what was written before the failure. That failure is reported as a named `WRITE:`
 * entry rather than thrown, and nothing outside the package directory is created, modified or
 * removed. So a caller reading a non-zero result as "nothing shipped" is right about the rest of its
 * tree and wrong about the package directory, whose contents it must not treat as a package.
 *
 * @param {object} opts
 * @param {string} opts.root - the repo root holding `extensions/catalog.json`.
 * @param {string} opts.publishDir - the throwaway build output the declared names are searched in first.
 * @param {string} opts.packageDir - where the declared set is written; created or emptied.
 * @param {string} opts.idOrName - the catalog entry's `id` or `name`.
 * @param {string} opts.version - written into the packaged manifest verbatim, with no normalisation.
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

  // The packaged manifest is sourced from the entry's declared manifestPath rather than from the
  // publish output, because the publish output's copy is a build side effect: the manifest the host
  // reads at install time must be the one under source control, stamped with this release. Derived
  // here rather than below the refusal because the protected set needs it; the manifest is still not
  // read until after the refusal has passed.
  const manifestSource = entry.manifestPath
    ? path.join(absoluteRoot, entry.manifestPath)
    : path.join(absoluteRoot, entry.path, MANIFEST_BY_CONVENTION);
  const manifestName = path.basename(manifestSource);

  const packageDirKey = canonicalPathKey(absolutePackageDir);
  const collision = protectedPaths(absoluteRoot, absolutePublishDir, entry, manifestSource).find(
    (item) => isSameOrContains(packageDirKey, item.key),
  );
  if (collision) {
    // Only the first collision is named: the refusal is binary, and a package directory wide enough
    // to swallow one protected path usually swallows several, which lengthens the message without
    // changing what the caller must do.
    failures.push(
      "INVALID: packageDir is or contains " +
        collision.label +
        " (" +
        collision.target +
        "): " +
        absolutePackageDir,
    );
    return done();
  }

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
  function resolveArtifactSource(name) {
    const searched =
      name === manifestName
        ? [{ source: manifestSource, root: "manifest" }]
        : uiBundleDir && sourceManifest.jsBundle === name
          ? [{ source: path.join(uiBundleDir, name), root: "ui-bundle" }]
          : [
              { source: path.join(absolutePublishDir, name), root: "publish" },
              { source: path.join(absoluteRoot, entry.path, name), root: "extension" },
              { source: path.join(absoluteRoot, name), root: "repo-root", repoLevelOnly: true },
            ];

    for (const candidate of searched) {
      // Skipped rather than dropped from `searched`: a root that was not offered is still a root the
      // caller has to know was considered, so the MISSING message stays as wide as the search.
      if (candidate.repoLevelOnly && !REPO_ROOT_FALLBACK_NAMES.includes(name)) continue;
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

  if (!emptyPackageDir(absolutePackageDir, failures)) return done();

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
const ALL_FLAGS = [...REQUIRED_FLAGS, "--root"];

function usage() {
  console.error(
    "Usage: node scripts/assemble-package.mjs " +
      REQUIRED_FLAGS.map((flag) => flag + " <value>").join(" ") +
      " [--root <dir>]",
  );
}

export function main(argv) {
  const options = new Map();
  for (let i = 0; i < argv.length; i += 2) {
    if (!ALL_FLAGS.includes(argv[i]) || argv[i + 1] == null || options.has(argv[i])) {
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

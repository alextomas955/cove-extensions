// Catalog-driven package assembler: copies exactly the files a catalog entry's `artifacts` array
// declares into an empty package directory, stamping the release version onto the manifest. The set
// is declared rather than discovered, so whatever else the build emits alongside them (debug symbols,
// XML docs) cannot reach the package.
//
// This script never deletes. A package directory that is not empty is refused; the one real delete
// stays with the caller that owns the install location.
//
// `import.meta.main` rather than a comparison against process.argv[1]: Node realpaths the entry, so
// the hand-rolled form answers "no" when the script is reached through a junction and then assembles
// nothing while exiting 0.
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { readJson } from "./repo-files.mjs";

// import.meta.dirname, never a filesystem path read off a module URL's path component: on Windows
// that yields a leading-slash form which resolves to a doubled drive prefix.
const scriptRoot = path.resolve(import.meta.dirname, "..");

const BACKSLASH = String.fromCodePoint(92);
const QUOTE = String.fromCodePoint(34);

// Absolute-path markers are constructed from parts rather than written as bare literals so a
// downstream scan of this script's own source does not mistake the markers for a real leak.
//
// A Windows path inside json arrives escaped: every separator is doubled, and a network share's two
// leading backslashes arrive as four. That escaping is what forces the two anchors apart.
//
//   - The drive marker's word boundary keeps a url out: a multi-letter scheme has no word boundary
//     before its last letter, so `https:` is not a hit. A one-letter scheme is spelled exactly like a
//     drive letter and cannot be told apart.
//   - The share marker requires its backslash run to begin a value. Doubled backslashes mid-value are
//     json escaping one separator, so without that anchor it matches every escaped drive path.
const WINDOWS_DRIVE_ROOT = new RegExp(BACKSLASH + "b[A-Za-z]:[/" + BACKSLASH + BACKSLASH + "]");
const UNC_SHARE_ROOT = new RegExp(
  "(?<![^" + QUOTE + BACKSLASH + "s])" + BACKSLASH + BACKSLASH + "{2,}[A-Za-z0-9_.-]",
);
const UNIX_HOME_PREFIXES = ["/" + "home" + "/", "/" + "Users" + "/"];

const ABSOLUTE_PATH_MARKERS = [
  { label: "drive root", hits: (line) => WINDOWS_DRIVE_ROOT.test(line) },
  { label: "network share", hits: (line) => UNC_SHARE_ROOT.test(line) },
  {
    label: "unix home",
    hits: (line) => UNIX_HOME_PREFIXES.some((prefix) => line.includes(prefix)),
  },
];

// A declared artifact name lands at the package root, so an absolute path, a `..` segment or any
// separator would write outside the package directory. Rejected on shape before any source is read.
const PATH_ESCAPE_PREFIX = new RegExp("^([A-Za-z]:|[/" + BACKSLASH + BACKSLASH + "])");
const PATH_SEPARATORS = new RegExp("[/" + BACKSLASH + BACKSLASH + "]");

const MANIFEST_BY_CONVENTION = "extension.json";

// The only names the repository root may supply: files a repository carries once at its root by
// convention, so the package's copy and the repository's are the same document. Any other name
// belongs to an extension. Keep this list generic, or the packer stops being catalog-driven.
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
// order their failures are reported. Each is optional; only a field the manifest carries makes a
// claim.
const MANIFEST_FILE_FIELDS = ["entryDll", "jsBundle", "cssBundle"];

function resolveEntry(catalog, idOrName) {
  return catalog.extensions.find((entry) => entry.id === idOrName || entry.name === idOrName);
}

function entryManifestPath(absoluteRoot, entry) {
  return entry.manifestPath
    ? path.join(absoluteRoot, entry.manifestPath)
    : path.join(absoluteRoot, entry.path, MANIFEST_BY_CONVENTION);
}

/** The manifest file `assemblePackage` would ship for `idOrName`, or null when no entry matches. */
export function resolveEntryManifestPath(root, idOrName) {
  const absoluteRoot = path.resolve(root);
  const catalog = readJson(path.join(absoluteRoot, "extensions", "catalog.json"));
  const entry = resolveEntry(catalog, idOrName);
  return entry ? entryManifestPath(absoluteRoot, entry) : null;
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

// Scans the text that is about to be written, not the text on disk: the manifest's outgoing copy
// carries a caller-supplied version the source file does not.
function checkNoAbsolutePath(name, text, failures) {
  text.split(/\r?\n/).forEach((line, index) => {
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

// The catalog and the manifest are two hand-edited files that have to agree: a declaration can name
// only files that exist and still describe a package the host refuses to load.
//
// Failures are pushed, never returned, so one run reports everything wrong with a declaration.
function checkDeclarationIsLoadable(sourceManifest, manifestName, names, failures) {
  if (!names.includes(manifestName)) {
    failures.push(
      "UNLOADABLE: artifacts does not declare " +
        manifestName +
        " - a package with no load manifest cannot be installed.",
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
 * outside `packageDir` is written.
 *
 * Past that point the guarantee is narrower. A failed write leaves the package directory incomplete
 * and is reported as a named `WRITE:` entry rather than thrown, so a non-zero result does not mean
 * nothing shipped.
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

  const names = readDeclaredNames(entry, failures);
  if (names === null) return done();

  // Shape and declaration failures are returned before any source is read and before the package
  // directory is touched, so a malformed declaration cannot destroy an earlier good package.
  if (failures.length > 0) return done();

  // Any content at all is refused: a leftover from an earlier run would ship alongside the declared
  // set.
  if (fs.existsSync(absolutePackageDir) && fs.readdirSync(absolutePackageDir).length > 0) {
    failures.push(
      "INVALID: package directory is not empty: " +
        absolutePackageDir +
        " - this packer never deletes, so it must be handed an absent or empty directory. " +
        "Clear it in the caller that owns it.",
    );
    return done();
  }

  // The packaged manifest is sourced from the entry's declared manifestPath, not from the publish
  // output: the manifest the host reads at install time must be the one under source control,
  // stamped with this release.
  const manifestSource = entryManifestPath(absoluteRoot, entry);
  const manifestName = path.basename(manifestSource);
  const sourceManifest = readSourceManifest(absoluteRoot, manifestSource, failures);
  if (sourceManifest === null) return done();

  checkDeclarationIsLoadable(sourceManifest, manifestName, names, failures);

  const sources = {
    absoluteRoot,
    absolutePublishDir,
    entry,
    manifestName,
    manifestSource,
    sourceManifest,
    uiBundleDir: entry.uiPath ? path.join(absoluteRoot, entry.uiPath, "dist") : null,
  };
  const staged = stageArtifacts(names, sources, version, failures);
  checkStagedJson(staged, failures);

  if (failures.length > 0) return done();

  // Created only once everything that could refuse has passed, so a failed run leaves no empty
  // directory behind for a caller to mistake for a package.
  fs.mkdirSync(absolutePackageDir, { recursive: true });
  writeStaged(staged, absolutePackageDir, absoluteRoot, failures, copied);

  return done();
}

/** The declared artifact names, deduplicated, or null when the entry declares no usable set. */
function readDeclaredNames(entry, failures) {
  // The declared set is the only source of what ships. An entry that declares nothing, or declares
  // an empty array, is a hard failure rather than a run that copies nothing and exits 0.
  const declared = entry.artifacts;
  if (declared == null) {
    failures.push(
      "MISSING: catalog entry " +
        entry.id +
        " declares no artifacts array - the shipped file set must be declared.",
    );
    return null;
  }
  if (!Array.isArray(declared) || declared.length === 0) {
    failures.push(
      "INVALID: catalog entry " +
        entry.id +
        " declares an empty artifacts array - an assemble that copies nothing inspected nothing.",
    );
    return null;
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
  return names;
}

/** The parsed source manifest, or null after recording why it could not be read. */
function readSourceManifest(absoluteRoot, manifestSource, failures) {
  if (!fs.existsSync(manifestSource)) {
    failures.push(
      "MISSING: source manifest is absent: " + path.relative(absoluteRoot, manifestSource),
    );
    return null;
  }
  try {
    return readJson(manifestSource);
  } catch (error) {
    failures.push(
      "INVALID: source manifest " +
        path.relative(absoluteRoot, manifestSource) +
        " is not parseable json: " +
        error.message,
    );
    return null;
  }
}

// The roots a declared artifact may come from, in precedence order. The manifest and the UI bundle
// each resolve from exactly one place or not at all; everything else is searched in order. Both
// bundle fields are UI build output and are never produced by the dotnet publish.
function candidateSourcesFor(name, sources) {
  if (name === sources.manifestName) {
    return [{ source: sources.manifestSource, root: "manifest" }];
  }
  const { sourceManifest, uiBundleDir } = sources;
  if (uiBundleDir && (sourceManifest.jsBundle === name || sourceManifest.cssBundle === name)) {
    return [{ source: path.join(uiBundleDir, name), root: "ui-bundle" }];
  }
  return [
    { source: path.join(sources.absolutePublishDir, name), root: "publish" },
    { source: path.join(sources.absoluteRoot, sources.entry.path, name), root: "extension" },
    { source: path.join(sources.absoluteRoot, name), root: "repo-root", repoLevelOnly: true },
  ];
}

function resolveArtifactSource(name, sources) {
  const searched = candidateSourcesFor(name, sources);
  for (const candidate of searched) {
    // Skipped rather than dropped from `searched`, so the missing message names every root.
    if (candidate.repoLevelOnly && !REPO_ROOT_FALLBACK_NAMES.has(name)) continue;
    if (fs.existsSync(candidate.source)) return { ...candidate, searched };
  }
  return { source: null, root: null, searched };
}

function stageArtifacts(names, sources, version, failures) {
  const staged = [];
  for (const name of names) {
    const { source, root: sourceRoot, searched } = resolveArtifactSource(name, sources);
    if (source == null) {
      const roots = searched.map(
        (candidate) => path.relative(sources.absoluteRoot, candidate.source) || candidate.source,
      );
      failures.push(
        "MISSING: declared artifact " +
          name +
          " was not found in any source root: " +
          roots.join(", "),
      );
      continue;
    }

    // The version is assigned as given: no semver parse, no coercion.
    const text =
      name === sources.manifestName
        ? JSON.stringify({ ...sources.sourceManifest, version }, null, 2) + "\n"
        : null;
    staged.push({ name, source, root: sourceRoot, text });
  }
  return staged;
}

function checkStagedJson(staged, failures) {
  for (const item of staged) {
    if (!item.name.endsWith(".json")) continue;
    let text = item.text;
    if (text == null) {
      try {
        text = fs.readFileSync(item.source, "utf8");
      } catch (error) {
        // Resolution is an existence check, which a directory and an unreadable file both satisfy,
        // so this is the first place either shows up. Reported rather than thrown, so it cannot
        // escape the exported function.
        failures.push(
          "UNREADABLE: declared artifact " +
            item.name +
            " could not be read from " +
            item.source +
            ": " +
            error.message,
        );
        continue;
      }
    }
    checkNoAbsolutePath(item.name, text, failures);
  }
}

function writeStaged(staged, absolutePackageDir, absoluteRoot, failures, copied) {
  for (const item of staged) {
    const destination = path.join(absolutePackageDir, item.name);
    try {
      if (item.text == null) {
        fs.copyFileSync(item.source, destination);
      } else {
        fs.writeFileSync(destination, item.text);
      }
    } catch (error) {
      // The loop stops rather than carrying on: a run that failed one write and wrote the rest
      // would print a count for a package that is not there.
      failures.push(
        "WRITE: " + item.name + " could not be written to " + destination + ": " + error.message,
      );
      break;
    }
    // Recorded after the write, so the reported count is what landed rather than what was declared.
    copied.push({
      name: item.name,
      source: path.relative(absoluteRoot, item.source),
      root: item.root,
    });
  }
}

const REQUIRED_FLAGS = ["--publish-dir", "--package-dir", "--extension", "--version"];
// --root is the one flag with a default: the checkout this script lives in.
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

  if (REQUIRED_FLAGS.some((flag) => !options.get(flag))) {
    usage();
    return 1;
  }

  const root = options.get("--root") ? path.resolve(options.get("--root")) : scriptRoot;
  const packageDir = path.resolve(options.get("--package-dir"));
  const result = assemblePackage({
    root,
    publishDir: path.resolve(options.get("--publish-dir")),
    packageDir,
    idOrName: options.get("--extension"),
    version: options.get("--version"),
  });

  if (!result.ok) {
    for (const failure of result.failures) console.error(failure);
    console.error(
      result.copied.length === 0
        ? "Assemble FAILED - nothing was written."
        : "Assemble FAILED after writing " +
            result.copied.length +
            " file(s). " +
            packageDir +
            " is INCOMPLETE, and this packer never deletes: clear it before retrying.",
    );
    return 1;
  }

  console.log(
    "Assembled " +
      options.get("--extension") +
      " " +
      options.get("--version") +
      " into " +
      path.relative(root, packageDir) +
      " - " +
      result.copied.length +
      " file(s):",
  );
  for (const file of result.copied)
    console.log("  " + file.name + "  <- " + file.root + ": " + file.source);
  return 0;
}

if (import.meta.main) {
  process.exit(main(process.argv.slice(2)));
}

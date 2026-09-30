// Seeds a disposable media file into a running instance and registers it through Cove's own import
// API, so a spec has a genuine file plus DB row to act on.
//
// Cove has no "create a row with no file" endpoint: import requires the file to exist on disk first.
// The copy goes through the container's own API rather than a host bind-mount, so the host's Docker
// file-sharing configuration does not enter into it.
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const FIXTURES_DIR = join(__dirname, "fixtures-media");

async function seedFile({ container, baseUrl, token, route, fixtureName, destName, destDir }) {
  const name = destName ?? `${Date.now()}-${randomUUID()}-${fixtureName}`;
  const hostPath = join(FIXTURES_DIR, fixtureName);
  const containerPath = `${destDir}/${name}`;

  await container.copyFilesToContainer([{ source: hostPath, target: containerPath }]);
  await container.exec(["chown", "cove:cove", containerPath], { user: "root" });

  const res = await fetch(`${baseUrl}/api/${route}/from-file`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: JSON.stringify({ filePath: containerPath }),
  });

  if (!res.ok) {
    const body = await res.text().catch(() => "<unreadable body>");
    throw new Error(`seedFile: POST /api/${route}/from-file failed (${res.status}): ${body}`);
  }

  return res.json();
}

/**
 * Copies fixtures-media/<fixtureName> into the container at <destDir>/<destName> (default /data)
 * and registers it as a video via POST /api/videos/from-file. Returns the created video.
 *
 * `token` is required only against an auth-enabled instance (pass `harness.token`); the import
 * route answers 401 there without it.
 */
export async function seedVideo({
  container,
  baseUrl,
  token,
  fixtureName = "test-video.mp4",
  destName,
  destDir = "/data",
}) {
  return seedFile({ container, baseUrl, token, route: "videos", fixtureName, destName, destDir });
}

/** The image counterpart of {@link seedVideo}, through POST /api/images/from-file. */
export async function seedImage({
  container,
  baseUrl,
  token,
  fixtureName = "test-image.png",
  destName,
  destDir = "/data",
}) {
  return seedFile({ container, baseUrl, token, route: "images", fixtureName, destName, destDir });
}

/** The text-document counterpart of {@link seedVideo}, through POST /api/texts/from-file. */
export async function seedText({
  container,
  baseUrl,
  token,
  fixtureName = "test-text.txt",
  destName,
  destDir = "/data",
}) {
  return seedFile({ container, baseUrl, token, route: "texts", fixtureName, destName, destDir });
}

/**
 * Copies fixtures-media/<fixtureName> into the container at <destDir>/<destName> and does NOT
 * register it. The file is present under a Cove library root and unknown to the host.
 *
 * `seedVideo` above registers what it copies, through the same host call an extension's own import
 * makes - so a spec proving that an extension causes an import cannot use it: the item would exist
 * before the extension did anything. This places the file and leaves the registering to whatever is
 * under test.
 *
 * The destination directory is created and both it and the file are handed to the app's own user.
 * Cove reads as `cove`, and a copied file arrives root-owned.
 *
 * @param {{container: import("testcontainers").StartedTestContainer, fixtureName?: string,
 *          destPath: string}} options
 * @returns {Promise<string>} the container path the file now occupies
 */
export async function placeVideoUnregistered({
  container,
  fixtureName = "test-video.mp4",
  destPath,
}) {
  if (!destPath?.startsWith("/")) {
    throw new Error(
      `placeVideoUnregistered: destPath must be an absolute container path under a Cove library root; got ${JSON.stringify(destPath)}.`,
    );
  }

  const directory = destPath.slice(0, destPath.lastIndexOf("/")) || "/";
  await container.exec(["mkdir", "-p", directory], { user: "root" });
  await container.copyFilesToContainer([
    { source: join(FIXTURES_DIR, fixtureName), target: destPath },
  ]);
  await container.exec(["chown", "-R", "cove:cove", directory], { user: "root" });

  // Read back rather than assumed: a copy that landed nowhere and a chown that failed both leave a
  // spec asserting an absence that was never a presence.
  const listed = await container.exec(["ls", "-l", destPath]);
  if (listed.exitCode !== 0) {
    throw new Error(
      `placeVideoUnregistered: ${destPath} is not there after the copy (${listed.output.trim()}).`,
    );
  }

  return destPath;
}

// Cove's own, not an instance's: the one route that declares which paths Cove treats as a library.
const COVE_CONFIG_PATH = "/api/system/config";

/**
 * Declares `path` as one more Cove library root on the running instance, and answers with the roots
 * Cove reports afterwards.
 *
 * Cove takes its library roots from its configuration, so an arrangement where one root sits inside
 * another - which is what makes a single reported file resolvable under two of them - cannot be
 * expressed by placing files. This reads the whole configuration, appends one path and writes it
 * back, deriving the new entry's shape from an entry Cove itself returned rather than naming its
 * fields here.
 *
 * `expectedRoots` are the roots the caller knows the instance declares. A principal that may not read
 * the configuration is served the library paths REDACTED, and writing those back would replace the
 * instance's real roots with the redaction marker - so this refuses before the write unless it can
 * see every root it was told to expect.
 *
 * @param {{get: Function, put: Function}} api - a client for the Cove instance, carrying its token
 * @param {string} path - the container path to declare
 * @param {string[]} expectedRoots - roots that must appear in the read, or the write is refused
 * @returns {Promise<string[]>} every library root Cove declares afterwards
 */
export async function addCoveLibraryRoot(api, path, expectedRoots) {
  const read = await api.get(COVE_CONFIG_PATH);
  if (!read.ok) {
    throw new Error(
      `addCoveLibraryRoot: GET ${COVE_CONFIG_PATH} answered ${read.status}: ${read.text?.slice(0, 300)}`,
    );
  }

  const entries = read.json?.covePaths ?? [];
  const declared = entries.map((entry) => entry.path);
  const missing = expectedRoots.filter((root) => !declared.includes(root));
  if (missing.length > 0) {
    throw new Error(
      `addCoveLibraryRoot: refusing to write. ${COVE_CONFIG_PATH} reported [${declared.join(", ")}], which is missing ${missing.join(", ")} - writing that back would replace the instance's library roots.`,
    );
  }

  if (declared.includes(path)) return declared;

  const saved = await api.put(COVE_CONFIG_PATH, {
    ...read.json,
    covePaths: [...entries, { ...entries[0], path }],
  });
  if (!saved.ok) {
    throw new Error(
      `addCoveLibraryRoot: PUT ${COVE_CONFIG_PATH} answered ${saved.status}: ${saved.text?.slice(0, 300)}`,
    );
  }

  // Read back off the instance rather than trusting the write: a configuration that did not take is
  // a spec asserting a branch the extension never entered.
  const after = await api.get(COVE_CONFIG_PATH);
  const roots = (after.json?.covePaths ?? []).map((entry) => entry.path);
  if (!roots.includes(path)) {
    throw new Error(
      `addCoveLibraryRoot: after the write ${COVE_CONFIG_PATH} reports [${roots.join(", ")}], without ${path}.`,
    );
  }
  return roots;
}

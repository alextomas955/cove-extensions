// Seeds a disposable media file into a running instance and registers it through Cove's own import
// API, so a spec has a genuine file plus DB row to act on.
//
// Cove has no "create a row with no file" endpoint: video and image import require the file to exist
// on disk first. The copy goes through the container's own API rather than a host bind-mount, so the
// host's Docker file-sharing configuration does not enter into it.
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const FIXTURES_DIR = join(__dirname, "fixtures-media");

/**
 * Copies fixtures-media/<fixtureName> into the container at <destDir>/<destName> (default /data)
 * and registers it as a video via POST /api/videos/from-file. Returns the created video's id.
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
  const name = destName ?? `${Date.now()}-${randomUUID()}-${fixtureName}`;
  const hostPath = join(FIXTURES_DIR, fixtureName);
  const containerPath = `${destDir}/${name}`;

  await container.copyFilesToContainer([{ source: hostPath, target: containerPath }]);
  await container.exec(["chown", "cove:cove", containerPath], { user: "root" });

  const res = await fetch(`${baseUrl}/api/videos/from-file`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: JSON.stringify({ filePath: containerPath }),
  });

  if (!res.ok) {
    const body = await res.text().catch(() => "<unreadable body>");
    throw new Error(`seedVideo: POST /api/videos/from-file failed (${res.status}): ${body}`);
  }

  return res.json();
}

/**
 * Copies fixtures-media/<fixtureName> into the container at <destDir>/<destName> and does NOT
 * register it. The file is present under a Cove library root and unknown to the host.
 *
 * `seedVideo` above registers what it copies, through the same host call an extension's own import
 * makes — so a spec proving that an extension causes an import cannot use it: the item would exist
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

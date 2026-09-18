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

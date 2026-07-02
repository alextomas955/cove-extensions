// Seeds a real, disposable test media file into a running harness instance and registers it with
// Cove via the real API, so Renamer's planner has a genuine file+DB row to act on. Cove requires
// an on-disk file for video/image import (no "create a fake row with no file" endpoint exists —
// see extensions/.planning/ or extensions/Renamer/.planning/ research notes) — copying a tiny real
// fixture via the container's own copyFilesToContainer (not a host bind-mount) keeps this
// environment-independent.
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const FIXTURES_DIR = join(__dirname, 'fixtures-media');

/**
 * Copies fixtures-media/<fixtureName> into the container at <destDir>/<destName> (default /data)
 * and registers it as a video via POST /api/videos/from-file. Returns the created video's id.
 */
export async function seedVideo({ container, baseUrl, fixtureName = 'test-video.mp4', destName, destDir = '/data' }) {
  const name = destName ?? `${Date.now()}-${Math.random().toString(36).slice(2, 8)}-${fixtureName}`;
  const hostPath = join(FIXTURES_DIR, fixtureName);
  const containerPath = `${destDir}/${name}`;

  await container.copyFilesToContainer([{ source: hostPath, target: containerPath }]);
  await container.exec(['chown', 'cove:cove', containerPath], { user: 'root' });

  const res = await fetch(`${baseUrl}/api/videos/from-file`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ filePath: containerPath }),
  });

  if (!res.ok) {
    const body = await res.text().catch(() => '<unreadable body>');
    throw new Error(`seedVideo: POST /api/videos/from-file failed (${res.status}): ${body}`);
  }

  return res.json();
}

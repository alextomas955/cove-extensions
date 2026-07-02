// Installs a staged extension into a running Testcontainers-managed container, sidestepping host
// bind-mount file-sharing configuration entirely — this must work identically on any contributor's
// machine and any CI runner, not just ones with a particular Docker Desktop drive-sharing setup.
//
// Two install paths, matching HARN-02/HARN-03:
//   - installViaContainerCopy: copies files into /config/extensions/<id>, requires a container
//     (re)start to be discovered (mirrors Cove's own bind-mount install method, minus the mount).
//   - installViaUrl: POST /api/extensions/install-from-url against a running, already-healthy
//     instance — hot-installs with no restart, exercising the real install API surface.
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { stageExtension } from './stage-extension.mjs';

export async function installViaContainerCopy({ container, publishDir, manifestPath, uiBundlePath }) {
  const stagingRoot = mkdtempSync(join(tmpdir(), 'cove-e2e-stage-'));
  try {
    const { id, path } = stageExtension({ publishDir, manifestPath, uiBundlePath, stagingRoot });
    const target = `/config/extensions/${id}`;

    await container.exec(['mkdir', '-p', target]);
    await container.copyDirectoriesToContainer([{ source: path, target }]);
    await container.exec(['chown', '-R', 'cove:cove', target], { user: 'root' });

    return { id };
  } finally {
    rmSync(stagingRoot, { recursive: true, force: true });
  }
}

export async function installViaUrl({ baseUrl, zipUrl }) {
  const res = await fetch(`${baseUrl}/api/extensions/install-from-url`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ url: zipUrl }),
  });
  if (!res.ok) {
    const body = await res.text().catch(() => '<unreadable body>');
    throw new Error(`installViaUrl: POST /api/extensions/install-from-url failed (${res.status}): ${body}`);
  }
  return res.json();
}

// Installs a staged extension into a running (or freshly-created, not-yet-started) harness
// container via `docker cp`, sidestepping host bind-mount file-sharing configuration entirely —
// this must work identically on any contributor's machine and any CI runner, not just ones with a
// particular Docker Desktop drive-sharing setup.
//
// Two install paths, matching HARN-02/HARN-03:
//   - installViaContainerCopy: docker cp into /config/extensions/<id>, requires a container
//     (re)start to be discovered (mirrors Cove's own bind-mount install method, minus the mount).
//   - installViaUrl: POST /api/extensions/install-from-url against a running, already-healthy
//     instance — hot-installs with no restart, exercising the real install API surface.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { stageExtension } from './stage-extension.mjs';

function dockerExec(args) {
  return execFileSync('docker', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });
}

export function installViaContainerCopy({ containerName, publishDir, manifestPath, uiBundlePath }) {
  const stagingRoot = mkdtempSync(join(tmpdir(), 'cove-e2e-stage-'));
  try {
    const { id, path } = stageExtension({ publishDir, manifestPath, uiBundlePath, stagingRoot });

    dockerExec(['exec', containerName, 'mkdir', '-p', `/config/extensions/${id}`]);
    dockerExec(['cp', `${path}/.`, `${containerName}:/config/extensions/${id}`]);
    dockerExec(['exec', '-u', 'root', containerName, 'chown', '-R', 'cove:cove', `/config/extensions/${id}`]);

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

if (existsSync(process.argv[1]) && process.argv[1].endsWith('install-extension.mjs') && process.argv[2] === '--container-copy') {
  const args = Object.fromEntries(
    process.argv.slice(3).reduce((pairs, arg, i, arr) => {
      if (arg.startsWith('--')) pairs.push([arg.slice(2), arr[i + 1]]);
      return pairs;
    }, [])
  );
  const result = installViaContainerCopy({
    containerName: args.container,
    publishDir: args['publish-dir'],
    manifestPath: args.manifest,
    uiBundlePath: args['ui-bundle'],
  });
  console.log(JSON.stringify(result));
}

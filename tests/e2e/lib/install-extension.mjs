// Installs a staged extension into a running Testcontainers-managed container, sidestepping host
// bind-mount file-sharing configuration entirely — this must work identically on any contributor's
// machine and any CI runner, not just ones with a particular Docker Desktop drive-sharing setup.
//
// One install path, matching HARN-02: copies files into /config/extensions/<id>, requiring a
// container (re)start to be discovered (mirrors Cove's own bind-mount install method, minus the
// mount).
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { stageExtension } from "./stage-extension.mjs";

export async function installViaContainerCopy({ container, repoRoot, publishDir, manifestPath }) {
  const stagingRoot = mkdtempSync(join(tmpdir(), "cove-e2e-stage-"));
  try {
    const { id, path } = stageExtension({ repoRoot, publishDir, manifestPath, stagingRoot });
    const target = `/config/extensions/${id}`;

    await container.exec(["mkdir", "-p", target]);
    await container.copyDirectoriesToContainer([{ source: path, target }]);
    await container.exec(["chown", "-R", "cove:cove", target], { user: "root" });

    return { id };
  } finally {
    rmSync(stagingRoot, { recursive: true, force: true });
  }
}

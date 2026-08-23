// Copies a staged extension into `/config/extensions/<id>` in a running container, which mirrors
// Cove's own bind-mount install without the mount: the host's Docker file-sharing configuration
// never enters into it, so this behaves the same on any contributor's machine and any CI runner.
//
// Cove discovers what is there at startup, so the container must be (re)started afterwards.
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

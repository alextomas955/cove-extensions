// A Docker volume two or more containers in one test mount at the same path.
//
// WHY A VOLUME AND NOT A BIND MOUNT. The rest of this harness deliberately never bind-mounts a host
// directory: that depends on the host's Docker file-sharing configuration, so the suite would run
// only where the right drive happens to be shared. A named volume is created through the same Docker
// connection Testcontainers already uses and is portable to any machine and any CI runner.
//
// WHY A VOLUME AND NOT copyFilesToContainer. A copy puts bytes inside ONE container. The acquire and
// import chain needs the opposite: a file one container writes and another reads at the same absolute
// path. Whisparr imports a completed download by hardlink, which cannot cross a device, so the
// download directory and the import root have to be the same filesystem and not two copies of it.
//
// Testcontainers' `withBindMounts` passes its `source` through to Docker unchanged, and Docker reads a
// source that is not an absolute path as a volume name. That is how a GenericContainer joins one of
// these. The Cove container comes up through compose instead, which takes the name in an environment
// variable (see docker-compose.yml).
import { getContainerRuntimeClient } from "testcontainers";
import { randomUUID } from "node:crypto";

/** Marks every volume this module creates, so a sweep can find what a killed run left behind. */
const LABEL = "org.cove-extensions.e2e.shared-volume";

/**
 * Creates a volume named for this run alone and returns its name with a `remove()` beside it.
 *
 * The name carries a UUID rather than the test's name: specs run in parallel, and two of them sharing
 * a volume would see each other's files at the paths they each assert are their own.
 *
 * @returns {Promise<{ name: string, remove: () => Promise<void> }>}
 */
export async function createSharedVolume() {
  const client = await getContainerRuntimeClient();
  const name = `cove-e2e-shared-${randomUUID()}`;

  await client.container.dockerode.createVolume({
    Name: name,
    Labels: { [LABEL]: "true" },
  });

  return {
    name,

    // Tolerates an already-removed volume. Teardown runs in a `finally` beside the containers' own,
    // and a volume still attached to a container that failed to stop is a worse error to raise than
    // the one that got us here.
    async remove() {
      try {
        await client.container.dockerode.getVolume(name).remove({ force: true });
      } catch {
        // Left for the sweep below, or for `docker volume prune`.
      }
    },
  };
}

/**
 * Removes every volume this module created that is not attached to anything.
 *
 * For a run killed between creating a volume and removing it. Docker refuses to remove a volume a
 * container still holds, so this cannot take one a live test is using.
 */
export async function sweepSharedVolumes() {
  const client = await getContainerRuntimeClient();
  const listed = await client.container.dockerode.listVolumes({
    filters: { label: [`${LABEL}=true`] },
  });

  let removed = 0;
  for (const volume of listed.Volumes ?? []) {
    try {
      await client.container.dockerode.getVolume(volume.Name).remove();
      removed += 1;
    } catch {
      // Still attached to a running test. Not this sweep's to take.
    }
  }
  return removed;
}

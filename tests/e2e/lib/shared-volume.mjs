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

  // Testcontainers' own reaper removes what Testcontainers created, and this volume is not one of
  // those: it is created through the Docker connection directly, so a run killed between creating it
  // and removing it leaves it behind with nothing to collect it. Sweeping here is what makes that
  // self-healing. Once per process, because every harness in a run would otherwise re-list every
  // volume on the machine.
  await sweepOnce();

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

/** The one sweep a process makes, shared by every harness it starts. */
let swept;

function sweepOnce() {
  // A failure to sweep is not a reason to fail the run that asked: the worst it leaves is the state
  // that was already there.
  swept ??= sweepSharedVolumes().catch(() => 0);
  return swept;
}

/**
 * How old a volume must be before a sweep will take it.
 *
 * Being unattached is NOT enough. A volume is unattached for the moment between its creation and the
 * container that mounts it starting, and the suite runs its workers in parallel: a sweep that took
 * every unattached one deleted volumes other workers had just created, and their bring-up then failed
 * with the volume not found. Comfortably longer than the longest spec, so nothing live is in range.
 */
const SWEEPABLE_AFTER_MS = 2 * 60 * 60 * 1000;

/**
 * Removes the volumes this module created that are old enough to be certainly orphaned.
 *
 * For a run killed between creating a volume and removing it. Docker refuses to remove one a
 * container still holds, and the age bound covers the window before it holds it.
 */
export async function sweepSharedVolumes(now = Date.now()) {
  const client = await getContainerRuntimeClient();
  const listed = await client.container.dockerode.listVolumes({
    filters: { label: [`${LABEL}=true`] },
  });

  let removed = 0;
  for (const volume of listed.Volumes ?? []) {
    const createdAt = Date.parse(volume.CreatedAt ?? "");
    if (Number.isNaN(createdAt) || now - createdAt < SWEEPABLE_AFTER_MS) {
      continue;
    }

    try {
      await client.container.dockerode.getVolume(volume.Name).remove();
      removed += 1;
    } catch {
      // Still attached to a running test. Not this sweep's to take.
    }
  }
  return removed;
}

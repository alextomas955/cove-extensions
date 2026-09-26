// Reading back the folder this extension builds for an entity, and the library it was built over.
//
// Cove cannot see the tree: the ignore file at its root is what keeps the host's own scan out of it,
// which is the whole point of putting one there. So a spec reads the tree through a shell in a
// container that mounts the volume, and reads the library the same way, rather than through any
// route this product or the host offers.
//
// Identity rather than name, everywhere a file is compared. A second name for one file and a second
// copy of it look alike in a listing and differ in what they cost a reader's disk, which is the one
// thing this capability must never get wrong.

/**
 * The library arrangements a spec can have seeded for it, by the name it names one under.
 *
 * A layout decides two things and nothing else: which folder every entity's files go in, and what
 * those files are called. Which entities the instance holds, which studio in Cove names each of
 * them and which identity each file carries are the same whichever layout is seeded, so one
 * scenario body asserts the same things over all of them.
 *
 * `folder` is where the files sit under the library root the instance is rooted on, or null for
 * that root itself. Every layout here puts more than one entity's files in one folder, which is
 * what an instance refuses to register: both generations refuse a second entity at a folder another
 * already uses.
 *
 * `names` are the file names to use in order, or null to let each generation name the files the way
 * its own parse expects.
 */
export const LIBRARY_LAYOUTS = {
  flat: { folder: null, names: null },
  byYear: { folder: "2019", names: null },

  // A camera's own file name, a hash, a name carrying a duplicate marker, and a date with a
  // counter. None of them carries a studio or a date an instance can parse. What the run hands an
  // instance is a folder of links named for the identities of the files they point at, which no
  // instance parses anything out of either, so a library nothing ever named is no different from
  // one that is named.
  unnamed: {
    folder: null,
    names: [
      "IMG_0042.mp4",
      "8f14e45fceea167a5a36dedd4bea2543.mp4",
      "clip (1).mp4",
      "2024-05-05_0007.mp4",
    ],
  },
};

/** Runs one shell line in a container and answers its output, refusing a non-zero exit. */
async function shell(container, line) {
  const ran = await container.exec(["sh", "-c", line], { user: "root" });
  if (ran.exitCode !== 0) {
    throw new Error(`tree-steps: \`${line}\` exited ${String(ran.exitCode)}: ${ran.output.trim()}`);
  }
  return ran.output;
}

/**
 * Lets both products write the library root they share.
 *
 * The instance owns what it imports and the host builds the tree, so both write the same volume. An
 * installation arranges that with a shared group or a matching user; the fixture hands the volume to
 * the instance's user alone, which leaves the host unable to create anything at the root and the
 * tree unbuildable for a reason that has nothing to do with the product.
 */
export async function sharedBetweenBothProducts(container, root) {
  await shell(container, `chmod a+rwx '${root}'`);
}

/**
 * Renames `path` to `to` on the container's own filesystem.
 *
 * A rename by the reader, a rename by the Renamer and a move within one drive are one act to the
 * filesystem: the name changes and the identity does not. Driving it here tests the same thing as
 * driving the Renamer, and needs no second extension installed.
 */
export async function renameOnDisk(container, path, to) {
  await shell(container, `mv '${path}' '${to}'`);
}

/** Takes the name at `path` away, as a reader deleting a file does. */
export async function removeOnDisk(container, path) {
  await shell(container, `rm -f '${path}'`);
}

/** Puts `path`'s last-changed time far enough back that no settle window can cover it. */
export async function changedLongAgo(container, path) {
  await shell(container, `touch -d '2020-01-01 00:00:00' '${path}'`);
}

/**
 * Leaves a file in `folder` under a name nothing derived from the file, as a refused arrival does.
 *
 * Its last-changed time is put well back, so what keeps it is the name rather than the clock.
 */
export async function leaveAFileIn(container, folder, name) {
  const path = `${folder}/${name}`;
  await shell(container, `head -c 65536 /dev/urandom > '${path}'`);
  await changedLongAgo(container, path);
  return path;
}

/** The names directly inside `folder`, or an empty list where nothing is there. */
export async function namesIn(container, folder) {
  const listed = await shell(container, `ls -1A '${folder}' 2>/dev/null || true`);
  return listed
    .split("\n")
    .map((name) => name.trim())
    .filter((name) => name.length > 0);
}

/** What one path is: the device it is on, its number there, and how many names it has. */
export async function identityOf(container, path) {
  const read = await shell(container, `stat -c '%d %i %h' '${path}' 2>/dev/null || echo absent`);
  const line = read.trim();
  if (line === "absent") return null;

  const [device, number, names] = line.split(/\s+/);
  return { device, number, names: Number(names) };
}

/**
 * What this extension calls a link to the file `identity` describes.
 *
 * Transcribed from the rule the product writes by, not computed from the product: the spelling is
 * the volume and the file number in hexadecimal, and it is what makes "did this extension write
 * this name?" answerable from the name alone.
 */
export function linkNameOf(identity, extension) {
  return `${BigInt(identity.device).toString(16)}-${BigInt(identity.number).toString(16)}${extension}`;
}

/** What the ignore file at `treeRoot` says, or null where there is none. */
export async function ignoreFileIn(container, treeRoot) {
  const read = await shell(
    container,
    `cat '${treeRoot}/.coveignore' 2>/dev/null || echo __absent__`,
  );
  const text = read.trim();
  return text === "__absent__" ? null : text;
}

/**
 * How many distinct media files sit under `root`, counted by identity rather than by name.
 *
 * A hard link is a second name for a file already there, so a count of names rises when one is made
 * and a count of identities does not. The claim this measures is that nothing was copied.
 *
 * Media alone, because the product writes one file of its own at each tree root, and a count over
 * everything would rise by that file and say nothing about the reader's bytes.
 */
export async function distinctMediaUnder(container, root) {
  const counted = await shell(
    container,
    `find '${root}' -type f -name '*.mp4' -printf '%i\\n' 2>/dev/null | sort -u | wc -l`,
  );
  return Number(counted.trim());
}

/** Every media path under `root`, so a failure names what appeared rather than only how many. */
export async function mediaUnder(container, root) {
  const listed = await shell(
    container,
    `find '${root}' -type f -name '*.mp4' 2>/dev/null | sort || true`,
  );
  return listed
    .split("\n")
    .map((path) => path.trim())
    .filter((path) => path.length > 0);
}

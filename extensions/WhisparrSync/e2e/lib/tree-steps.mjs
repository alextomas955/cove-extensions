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

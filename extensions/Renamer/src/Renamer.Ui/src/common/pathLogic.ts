/**
 * Path-splitting for display, shared by the surfaces that show a file's old and new name.
 *
 * Separate from `format.ts` — and named for the purity rule rather than trusted to it — because
 * `format.ts` reaches the host request module for `ApiError`, and `@cove/runtime/*` is a build-time
 * external the host supplies at runtime: nothing that resolves under the test runner. A pure consumer
 * that imported these from there would lose its whole test file to an unresolvable import, which is
 * how this split was found.
 */

/** Last path segment, tolerant of both `/` and `\` separators (Windows paths). */
export function basename(p: string): string {
  if (!p) return p;
  const i = Math.max(p.lastIndexOf("/"), p.lastIndexOf("\\"));
  return i >= 0 ? p.slice(i + 1) : p;
}

/** The folder portion of a path (everything before the last separator); "" if there is none. */
export function dirname(p: string): string {
  if (!p) return p;
  const i = Math.max(p.lastIndexOf("/"), p.lastIndexOf("\\"));
  return i >= 0 ? p.slice(0, i) : "";
}

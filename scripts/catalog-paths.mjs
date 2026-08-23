// The one validator for a catalog path field, shared rather than restated so every reader of
// extensions/catalog.json agrees on what a path field is allowed to be.
import path from "node:path";

/**
 * Checks that a catalog path field is repo-relative, returning a reason or null.
 *
 * Consumers derive a directory from these fields and then empty it, collect test files out of it, or
 * copy from it, so the check has to happen before the value is used: a path that escapes the
 * repository cannot be made safe by happening to resolve somewhere harmless.
 */
export function checkRelativePath(field, value) {
  if (typeof value !== "string" || value === "") {
    return `${field} must be a non-empty string, found: ${JSON.stringify(value)}`;
  }
  // path.isAbsolute answers for the platform it runs on, so the drive-letter form is tested separately:
  // a Windows-absolute value is not absolute to a Linux runner and would otherwise pass here.
  if (path.isAbsolute(value) || /^[A-Za-z]:/.test(value)) {
    return `${field} must be repo-relative, found an absolute path: ${value}`;
  }
  if (value.split(/[/\\]/).includes("..")) {
    return `${field} must contain no ".." segment, found: ${value}`;
  }
  return null;
}

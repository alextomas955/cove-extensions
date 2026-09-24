// Readers for the repository's own JSON and MSBuild files, and the one validator for a catalog path
// field, shared so every script agrees on them.
import fs from "node:fs";
import path from "node:path";

/** Parses a JSON file, tolerating the byte-order mark some Windows editors write. */
export function readJson(filePath) {
  return JSON.parse(fs.readFileSync(filePath, "utf8").replace(/^\uFEFF/, ""));
}

/**
 * Reads the flat `<Name>value</Name>` property elements out of an MSBuild file's text.
 *
 * Later declarations win, and a `$(Other)` reference expands from what has already been read.
 */
export function parseMsBuildProperties(content) {
  const props = {};
  const pattern = /<([A-Za-z_][A-Za-z0-9_.-]*)(?:\s[^>]*)?>([^<]*)<\/\1>/g;
  for (const match of content.matchAll(pattern)) {
    const [, name, rawValue] = match;
    props[name] = rawValue
      .trim()
      .replace(/\$\(([^)]+)\)/g, (_, propertyName) => props[propertyName] ?? `$(${propertyName})`);
  }
  return props;
}

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

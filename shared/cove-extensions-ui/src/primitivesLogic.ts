/**
 * Pure, DOM-free logic the primitive components render on top of. Kept import-free (no React, no DOM)
 * so the offline test runner can compile it in isolation exactly like options.ts — the same logic the
 * components use is the logic the offline suite covers.
 */

/** The outcome of validating a rule pattern. */
export interface RegexValidity {
  valid: boolean;
  /** Present only when invalid: the thrown construction message. */
  message?: string;
}

/**
 * Case-insensitive substring filter over an arbitrary item list. The label accessor keeps it generic
 * so the studio/tag picker and any future list reuse it without forking. A blank query yields the
 * whole list (a no-op filter, not an empty result) and input order is preserved.
 *
 * Diacritic-naive on purpose: a plain lowercased substring match mirrors the host's own simple
 * filtering — locale collation would diverge from what Cove does elsewhere.
 */
export function filterByText<T>(
  query: string,
  items: readonly T[],
  label: (item: T) => string,
): T[] {
  const q = query.trim().toLowerCase();
  if (q.length === 0) return [...items];
  return items.filter((item) => label(item).toLowerCase().includes(q));
}

/**
 * Validate a rule pattern as best a browser can: `new RegExp` is the only validator available in the
 * bundle, so it catches obvious parse errors (an unbalanced group, a dangling quantifier) but is NOT
 * full .NET parity — the rename engine is .NET, so a JS-valid pattern is not a guarantee of .NET
 * validity, and a handful of .NET constructs JS rejects are not actually broken. Treat the result as
 * an early "this is obviously malformed" signal, never as the authoritative verdict.
 *
 * An empty pattern is valid: an empty rule pattern is a no-op, not an error.
 */
export function isRegexValid(pattern: string): RegexValidity {
  if (pattern.length === 0) return { valid: true };
  try {
    new RegExp(pattern);
    return { valid: true };
  } catch (err) {
    return { valid: false, message: err instanceof Error ? err.message : String(err) };
  }
}

/**
 * Best-effort, platform-tolerant check for whether a string looks like an absolute path (Windows
 * drive-letter, or POSIX/UNC leading slash). This is an advisory-only hint, not a validator — a
 * blank/whitespace-only value is treated as "not implausible" (returns true) so a caller composing
 * this with its own blank-suppresses-the-hint logic never needs a duplicate blank check here too.
 */
export function isAbsolutePathShape(value: string): boolean {
  const trimmed = value.trim();
  if (trimmed.length === 0) return true;
  return /^[A-Za-z]:[\\/]/.test(trimmed) || /^[\\/]/.test(trimmed);
}

/** Extensions Cove's video/image/audio primary-file kinds commonly use. */
const PRIMARY_MEDIA_EXTENSIONS: ReadonlySet<string> = new Set([
  "mp4",
  "mkv",
  "avi",
  "mov",
  "wmv",
  "jpg",
  "jpeg",
  "png",
  "gif",
  "webp",
  "mp3",
  "flac",
  "wav",
  "m4a",
]);

/**
 * Advisory check for a sidecar extension already normalized (lowercased, dot-stripped) by the
 * caller. Returns null when the value looks like a plausible sidecar extension. Shape rejection
 * takes priority over the primary-media check — a shape-invalid value is never also flagged as a
 * media duplicate, since it isn't a valid extension body at all.
 */
export function extensionShapeAdvisory(value: string): string | null {
  if (value.length === 0) return null;
  if (!/^[a-z0-9]+$/.test(value)) {
    return "Extensions are letters and numbers only, like srt or nfo.";
  }
  if (PRIMARY_MEDIA_EXTENSIONS.has(value)) {
    return "This looks like a primary media extension, not a sidecar.";
  }
  return null;
}

/**
 * Pure, client-side template advisories. No React, no I/O — these only
 * DETECT authoring mistakes for an inline amber advisory; the engine remains the render-time
 * authority and renders every template leniently (it never throws). Validation is advisory and
 * NEVER blocks Save or moves the caret.
 *
 * Two checks live here (brace balance + unknown tokens); the third advisory (empty-for-sample)
 * is read off the existing debounced /preview-sample `flags` in the panel — there is no new
 * request and no engine change.
 *
 * Engine grammar facts these mirror (kept in sync with the C# tokenizer):
 *   - A token is a BARE `$` + [A-Za-z0-9_]+  (Tokenizer.cs:65-79). There is NO `${token}` form.
 *   - `$$` is the literal-`$` escape (Tokenizer.cs:56-63) — the second `$` must NOT start a token.
 *   - `{` increments depth, `}` decrements; a stray `}` (depth < 0) or a leftover open `{`
 *     (depth > 0) is unbalanced (Tokenizer.cs:81-110).
 *   - Tokens resolve case-insensitively (TemplateEngine.cs:147).
 */
import { TOKENS } from "./TokenLegend";

/** The canonical known-token set, lower-cased, leading `$` dropped — single-sourced from TOKENS. */
const KNOWN = new Set(TOKENS.map((t) => t.token.slice(1).toLowerCase()));

/**
 * The bare token NAMES (leading `$` stripped), in TOKENS declaration order — single-sourced from
 * the same `TOKENS` constant, no re-listed literals. Used by the TokenPicker menu, whose
 * fields (RequiredFields / DropOrder) take bare names (`title`), not `$title`. Preserves original
 * case for display; matching is done case-insensitively by {@link isKnownToken} / the engine.
 */
export const BARE_TOKENS: readonly string[] = TOKENS.map((t) => t.token.slice(1));

/**
 * True when `name` is a known engine token (compared lower-cased against the same `KNOWN` set the
 * template validator uses — so the picker, the token advisory, and `unknownTokens` never drift).
 * Accepts a bare name (`title`); a leading `$` is tolerated and stripped first.
 */
export function isKnownToken(name: string): boolean {
  const bare = name.startsWith("$") ? name.slice(1) : name;
  return KNOWN.has(bare.toLowerCase());
}

/**
 * True when every `{` has a matching `}` and there is no stray `}`. Mirrors the engine's depth
 * tracking exactly; returns false on a negative depth (stray close) or a non-zero final depth
 * (unclosed open).
 */
export function bracesBalanced(s: string): boolean {
  let depth = 0;
  for (const c of s) {
    if (c === "{") depth++;
    else if (c === "}") {
      depth--;
      if (depth < 0) return false;
    }
  }
  return depth === 0;
}

/**
 * The de-duped list of `$token` occurrences whose name is NOT in the canonical set (typos),
 * each returned WITH its leading `$` for display. Skips the `$$` literal escape so it is never
 * mis-flagged, and compares case-insensitively (matching the engine resolver).
 */
export function unknownTokens(s: string): string[] {
  const unknown: string[] = [];
  const seen = new Set<string>();
  for (let i = 0; i < s.length; i++) {
    if (s[i] !== "$") continue;
    if (s[i + 1] === "$") {
      i++; // `$$` literal escape — consume the pair, neither `$` starts a token
      continue;
    }
    // Scan the token name: [A-Za-z0-9_]+
    let j = i + 1;
    while (j < s.length && /[A-Za-z0-9_]/.test(s[j])) j++;
    if (j === i + 1) continue; // lone `$` (no name) — the engine emits a literal `$`, not a token
    const name = s.slice(i + 1, j);
    const lower = name.toLowerCase();
    if (!KNOWN.has(lower) && !seen.has(lower)) {
      seen.add(lower);
      unknown.push(`$${name}`);
    }
    i = j - 1; // advance past the consumed name
  }
  return unknown;
}

/**
 * True when a bare `$token` (case-insensitive, `$$`-escape-aware) appears in either template.
 * Mirrors {@link unknownTokens}' exact scan algorithm but tests one caller-supplied name instead
 * of the canonical `KNOWN` set, so it needs no `TOKENS`/`KNOWN` lookup and stays dependency-free.
 */
function scanForToken(s: string, wantLower: string): boolean {
  for (let i = 0; i < s.length; i++) {
    if (s[i] !== "$") continue;
    if (s[i + 1] === "$") {
      i++; // `$$` literal escape — consume the pair, neither `$` starts a token
      continue;
    }
    let j = i + 1;
    while (j < s.length && /[A-Za-z0-9_]/.test(s[j])) j++;
    if (j === i + 1) continue; // lone `$` (no name) — the engine emits a literal `$`, not a token
    if (s.slice(i + 1, j).toLowerCase() === wantLower) return true;
    i = j - 1; // advance past the consumed name
  }
  return false;
}

/**
 * True when `token` appears (bare, case-insensitive) in `filenameTemplate` or `folderTemplate`.
 * Either template containing the token counts as "in use."
 */
export function templateUsesToken(
  token: string,
  filenameTemplate: string,
  folderTemplate: string,
): boolean {
  const wantLower = (token.startsWith("$") ? token.slice(1) : token).toLowerCase();
  return scanForToken(filenameTemplate, wantLower) || scanForToken(folderTemplate, wantLower);
}

/** Levenshtein edit distance (small strings only). */
function editDistance(a: string, b: string): number {
  const m = a.length;
  const n = b.length;
  const row = Array.from({ length: n + 1 }, (_, j) => j);
  for (let i = 1; i <= m; i++) {
    let prev = row[0];
    row[0] = i;
    for (let j = 1; j <= n; j++) {
      const tmp = row[j];
      row[j] = a[i - 1] === b[j - 1] ? prev : Math.min(prev, row[j - 1], row[j]) + 1;
      prev = tmp;
    }
  }
  return row[n];
}

/**
 * Best-effort "Did you mean" for an unknown token name (with or without the leading `$`). Returns
 * the nearest known token (WITH a leading `$`) only when the edit distance is small (≤ 2);
 * otherwise undefined. Derived purely from the static TOKENS set — never echoes user markup.
 */
export function suggestFor(token: string): string | undefined {
  const name = (token.startsWith("$") ? token.slice(1) : token).toLowerCase();
  let best: string | undefined;
  let bestDist = Infinity;
  for (const known of KNOWN) {
    const d = editDistance(name, known);
    if (d < bestDist) {
      bestDist = d;
      best = known;
    }
  }
  return best !== undefined && bestDist > 0 && bestDist <= 2 ? `$${best}` : undefined;
}

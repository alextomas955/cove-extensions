/**
 * Client-side template advisories, shown inline and never blocking Save. The engine stays the render
 * authority and renders any template without throwing; the empty-for-sample advisory is read off the
 * /preview-sample flags instead.
 *
 * These mirror the C# Tokenizer: a token is `$` followed by letters, digits or `_`; `$$` is a literal
 * `$`, after which a following name is still a token; braces nest; and tokens resolve without regard
 * to case.
 */
import { TOKENS } from "./tokens";

/** The known token names, lower-cased and without `$`. */
const KNOWN = new Set(TOKENS.map((t) => t.token.slice(1).toLowerCase()));

/** The token names without `$`, as the Required fields and Drop order inputs store them. */
export const BARE_TOKENS: readonly string[] = TOKENS.map((t) => t.token.slice(1));

/** True when `name`, with or without its `$`, is a known token. */
export function isKnownToken(name: string): boolean {
  const bare = name.startsWith("$") ? name.slice(1) : name;
  return KNOWN.has(bare.toLowerCase());
}

/** True when every `{` has a matching `}` and no `}` comes before its `{`. */
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

const NAME_CHAR = /[\p{L}\p{N}_]/u;

/** Every token name in `s`, in order and without its `$`. */
function tokenNames(s: string): string[] {
  const names: string[] = [];
  let i = 0;
  while (i < s.length) {
    if (s[i] !== "$") {
      i++;
    } else if (s[i + 1] === "$") {
      // The pair is one literal `$`. When a name follows, the second `$` starts a token.
      i += NAME_CHAR.test(s[i + 2] ?? "") ? 1 : 2;
    } else {
      let j = i + 1;
      while (j < s.length && NAME_CHAR.test(s[j])) j++;
      if (j > i + 1) names.push(s.slice(i + 1, j));
      i = Math.max(j, i + 1);
    }
  }
  return names;
}

/** The distinct unknown tokens in `s`, each with its `$`, compared without regard to case. */
export function unknownTokens(s: string): string[] {
  const seen = new Set<string>();
  const unknown: string[] = [];
  for (const name of tokenNames(s)) {
    const lower = name.toLowerCase();
    if (!KNOWN.has(lower) && !seen.has(lower)) {
      seen.add(lower);
      unknown.push(`$${name}`);
    }
  }
  return unknown;
}

/** True when `token`, with or without its `$`, appears in either template. */
export function templateUsesToken(
  token: string,
  filenameTemplate: string,
  folderTemplate: string,
): boolean {
  const want = (token.startsWith("$") ? token.slice(1) : token).toLowerCase();
  return [filenameTemplate, folderTemplate].some((t) =>
    tokenNames(t).some((name) => name.toLowerCase() === want),
  );
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
 * the nearest known token (with a leading `$`) only when the edit distance is small (≤ 2);
 * otherwise undefined. Derived purely from the static tokens set - never echoes user markup.
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

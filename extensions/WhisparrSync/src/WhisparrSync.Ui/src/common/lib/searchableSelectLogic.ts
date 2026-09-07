/**
 * Ranking mirrors Cove core's `utils/searchRanking.rankSearchOptions` (exact > prefix > word-start >
 * substring, ties by shorter label then locale); unlike Cove's sort-only ranking this one also DROPS
 * non-matches, because the facet control must narrow its option list as you type, not just reorder it.
 */
export interface SearchableOption {
  value: string;
  label: string;
}

/**
 * The options to show for a query, in rank order. An empty query returns the options unchanged (the
 * full set, caller order preserved); a non-empty query keeps only substring matches, best rank first.
 */
export function filterOptions<T extends SearchableOption>(
  options: readonly T[],
  query: string,
): T[] {
  const needle = query.trim().toLocaleLowerCase();
  if (needle.length === 0) {
    return [...options];
  }
  return options
    .map((option) => ({ option, rank: rankLabel(option.label.toLocaleLowerCase(), needle) }))
    .filter((entry) => entry.rank < NO_MATCH)
    .sort(
      (left, right) =>
        left.rank - right.rank ||
        left.option.label.length - right.option.label.length ||
        left.option.label.localeCompare(right.option.label),
    )
    .map((entry) => entry.option);
}

/** The next active index after an arrow move, wrapping at both ends over a list of `length` rows. */
export function nextActiveIndex(current: number, length: number, direction: 1 | -1): number {
  if (length === 0) {
    return -1;
  }
  const base = current < 0 ? (direction === 1 ? -1 : 0) : current;
  return (base + direction + length) % length;
}

const NO_MATCH = 4;

function rankLabel(label: string, needle: string): number {
  if (label === needle) return 0;
  if (label.startsWith(needle)) return 1;
  if (label.search(new RegExp(`(^|\\s)${escapeRegExp(needle)}`)) >= 0) return 2;
  if (label.includes(needle)) return 3;
  return NO_MATCH;
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

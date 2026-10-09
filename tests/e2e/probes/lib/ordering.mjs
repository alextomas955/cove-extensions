// The comparator every probe sorts its reported names with.
//
// `Array.prototype.sort` with no comparator sorts by UTF-16 code unit after converting each element
// to a string, which is right for these lists by accident rather than by statement. Ordinal rather
// than `localeCompare`: a probe's output is read against an earlier run's, and a locale-sensitive
// order would make two machines disagree about a record neither product changed.

/** Orders two strings by code point, for a listing a reader compares between runs. */
export const byText = (left, right) => {
  if (left === right) return 0;
  return left < right ? -1 : 1;
};

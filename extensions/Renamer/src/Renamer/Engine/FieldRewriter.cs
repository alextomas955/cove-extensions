using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Engine;

// Value-level rewrites of resolved token values, applied before the segment-level sanitize step.
// Rules key off the canonical Tokens names, case-insensitively.
public static class FieldRewriter
{
    // Order: literal find/replace, then the studio-name squeeze, then the leading-article strip.
    public static string RewriteScalar(string tokenName, string value, RenamerOptions o)
    {
        // Find and Replace are literal, not regex. A rule with an empty Find is skipped so a stored
        // empty rule cannot loop or throw.
        foreach (var rule in o.FieldReplacers)
        {
            if (rule.Find.Length == 0)
            {
                continue;
            }

            if (string.Equals(rule.TargetToken, tokenName, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Replace(rule.Find, rule.Replace, StringComparison.Ordinal);
            }
        }

        if (o.SqueezeStudioNames
            && string.Equals(tokenName, Tokens.Studio, StringComparison.OrdinalIgnoreCase))
        {
            value = value.Replace(" ", string.Empty);
        }

        // At most one leading article is stripped, and only from $title.
        if (o.StripLeadingArticles
            && string.Equals(tokenName, Tokens.Title, StringComparison.OrdinalIgnoreCase))
        {
            value = StripLeadingArticle(value, o.Articles);
        }

        return value;
    }

    // Whole-word boundaries mean "Eve" is dropped from "Eve Goes Home" but kept for "Evelyn Goes
    // Home". A performer whose name trims to empty is never dropped.
    public static IReadOnlyList<string> DropPerformersInTitle(
        IReadOnlyList<string> performers, string resolvedTitle, RenamerOptions o)
    {
        if (!o.PreventTitlePerformer)
        {
            return performers;
        }

        return performers.Where(p => !NameIsWholeWordInTitle(p, resolvedTitle)).ToList();
    }

    // Filtering records keeps per-position semantics: when two performers share a name, only the
    // matching positions drop and surviving duplicates stay in order.
    public static IReadOnlyList<RenamerPerformer> DropPerformersInTitle(
        IReadOnlyList<RenamerPerformer> performers, string resolvedTitle, RenamerOptions o)
    {
        if (!o.PreventTitlePerformer)
        {
            return performers;
        }

        return performers.Where(p => !NameIsWholeWordInTitle(p.Name, resolvedTitle)).ToList();
    }

    // An IndexOf scan with explicit non-letter-or-digit boundary checks, so no user-supplied string
    // is ever compiled as a regex. A name that trims to empty returns false so it matches nothing.
    private static bool NameIsWholeWordInTitle(string name, string title)
    {
        string trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        int from = 0;
        while (from <= title.Length - trimmed.Length)
        {
            int idx = title.IndexOf(trimmed, from, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            bool leftOk = idx == 0 || !char.IsLetterOrDigit(title[idx - 1]);
            int end = idx + trimmed.Length;
            bool rightOk = end == title.Length || !char.IsLetterOrDigit(title[end]);
            if (leftOk && rightOk)
            {
                return true;
            }

            from = idx + 1;
        }

        return false;
    }

    // Drops a folder segment equal to its immediate predecessor, ignoring case, keeping the first
    // occurrence: "Foo/Foo/Bar" becomes "Foo/Bar". Non-consecutive repeats such as "Foo/Bar/Foo" stay.
    public static List<string> CollapseConsecutive(IEnumerable<string> segments, RenamerOptions o)
    {
        if (!o.PreventConsecutiveSegments)
        {
            return segments.ToList();
        }

        var result = new List<string>();
        foreach (var seg in segments)
        {
            if (result.Count == 0
                || !string.Equals(result[^1], seg, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(seg);
            }
        }

        return result;
    }

    // The char after the article must be whitespace, so "Theatre" is left alone. Stops at the first
    // match.
    private static string StripLeadingArticle(string value, List<string> articles)
    {
        foreach (var article in articles)
        {
            if (article.Length == 0)
            {
                continue;
            }

            if (value.Length > article.Length
                && value.AsSpan(0, article.Length).Equals(article, StringComparison.OrdinalIgnoreCase)
                && char.IsWhiteSpace(value[article.Length]))
            {
                return value[article.Length..].TrimStart();
            }
        }

        return value;
    }
}

using Renamer.Options;
using Renamer.Planner;

namespace Renamer.Engine;

/// <summary>
/// Pure, static, host-free per-field VALUE rewriter. Operates on raw resolved token values
/// inside <see cref="TemplateEngine.BuildResolvedMap"/>, BEFORE <see cref="Sanitizer.CleanSegment"/>
/// — field rewrites are value-level transforms, sanitize is a segment-level transform; the two are
/// kept separate and do NOT merge. This class does NOT re-implement the sanitizer's
/// illegal/space/collapse logic. No I/O, no host types.
///
/// Per-field rules key off the canonical <see cref="Tokens"/> names case-insensitively
/// (never raw literals). <see cref="RewriteScalar"/> applies the value-level transforms in this
/// fixed order: field_replacer → squeeze_studio_names → prepositions_removal.
/// </summary>
public static class FieldRewriter
{
    /// <summary>
    /// Applies the value-level rewrites for one scalar token in this fixed order:
    /// (1) literal find/replace (<see cref="RenamerOptions.FieldReplacers"/>),
    /// (2) squeeze_studio_names (<see cref="RenamerOptions.SqueezeStudioNames"/>),
    /// (3) leading-article strip (<see cref="RenamerOptions.StripLeadingArticles"/>).
    /// Pure: no I/O, no host types, no regex.
    /// </summary>
    public static string RewriteScalar(string tokenName, string value, RenamerOptions o)
    {
        // Step 1 — per-field literal find/replace (literal, NOT regex), in list
        // order, for rules targeting this token. Skip rules with an empty Find so adversarial /
        // empty config cannot loop or throw.
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

        // Step 2 — squeeze_studio_names: remove all spaces from the $studio value.
        if (o.SqueezeStudioNames
            && string.Equals(tokenName, Tokens.Studio, StringComparison.OrdinalIgnoreCase))
        {
            value = value.Replace(" ", string.Empty);
        }

        // Step 3 — strip at most ONE leading article (The/A/An, configurable,
        // case-insensitive) followed by whitespace, from $title by default.
        if (o.StripLeadingArticles
            && string.Equals(tokenName, Tokens.Title, StringComparison.OrdinalIgnoreCase))
        {
            value = StripLeadingArticle(value, o.Articles);
        }

        return value;
    }

    /// <summary>
    /// When <see cref="RenamerOptions.PreventTitlePerformer"/> is set, drops any performer whose
    /// TRIMMED name appears as a whole-word, case-insensitive occurrence inside
    /// <paramref name="resolvedTitle"/> (the title after the scalar rewrites). Whole-word boundaries
    /// mean <c>Eve</c> is dropped from "Eve Goes Home" but NOT from "Evelyn Goes Home". A
    /// trimmed-empty performer name is never dropped. Returns the list unchanged when the flag is off.
    /// Pure: no I/O, no user-compiled regex.
    /// </summary>
    public static IReadOnlyList<string> DropPerformersInTitle(
        IReadOnlyList<string> performers, string resolvedTitle, RenamerOptions o)
    {
        if (!o.PreventTitlePerformer)
        {
            return performers;
        }

        return performers.Where(p => !NameIsWholeWordInTitle(p, resolvedTitle)).ToList();
    }

    /// <summary>
    /// Record-channel counterpart of <see cref="DropPerformersInTitle(IReadOnlyList{string}, string, RenamerOptions)"/>:
    /// drops performer RECORDS whose name appears as a whole word in the resolved title, using the
    /// same predicate. Filtering the records directly (rather than rebuilding from a deduped name set)
    /// keeps per-position semantics — when two performers share a name, only the matching positions
    /// are dropped, and surviving duplicates are preserved in order. Returns the list unchanged when
    /// the flag is off. Pure: no I/O, no user-compiled regex.
    /// </summary>
    public static IReadOnlyList<RenamerPerformer> DropPerformersInTitle(
        IReadOnlyList<RenamerPerformer> performers, string resolvedTitle, RenamerOptions o)
    {
        if (!o.PreventTitlePerformer)
        {
            return performers;
        }

        return performers.Where(p => !NameIsWholeWordInTitle(p.Name, resolvedTitle)).ToList();
    }

    /// <summary>
    /// True iff the trimmed <paramref name="name"/> occurs as a whole word (case-insensitive) in
    /// <paramref name="title"/>. A trimmed-empty name short-circuits to false so an empty performer
    /// can never match everything (bounded, ReDoS-free — an <c>IndexOf</c> scan with explicit
    /// non-letter-or-digit boundary checks, NOT a user-compiled regex).
    /// </summary>
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

    /// <summary>
    /// <c>prevent_consecutive</c>: when <see cref="RenamerOptions.PreventConsecutiveSegments"/>
    /// is set, walks the cleaned folder segments and drops any segment equal to its immediate
    /// predecessor under <see cref="StringComparison.OrdinalIgnoreCase"/>, keeping the first
    /// occurrence (<c>Foo/Foo/Bar</c> → <c>Foo/Bar</c>; non-consecutive <c>Foo/Bar/Foo</c>
    /// untouched). Returns the segments unchanged (materialized) when the flag is off. A single
    /// linear pass — bounded, no I/O.
    /// </summary>
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

    /// <summary>
    /// Removes a single leading article followed by a whitespace char (case-insensitive),
    /// then re-trims the remaining leading whitespace. Stops after the first match.
    /// "Theatre" is untouched (the char after the article must be whitespace, not a letter).
    /// </summary>
    private static string StripLeadingArticle(string value, List<string> articles)
    {
        foreach (var article in articles)
        {
            if (article.Length == 0)
            {
                continue;
            }

            // value must start with the article followed by at least one whitespace char.
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

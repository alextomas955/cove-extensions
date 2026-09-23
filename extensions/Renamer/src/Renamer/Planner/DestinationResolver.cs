using System.Text.RegularExpressions;

using Renamer.Options;

namespace Renamer.Planner;

// The pure routing brain: maps one entity to the matched rule's own destination, or to no rule at all.
// Called once per entity by the planner.
//
// Precedence, first category that produces a match winning:
// excludes, unorganized, tag, studio including parents, source-path. Within a category the first
// user-ordered rule wins, and within studio a direct match outranks an ancestor. An item matching no
// rule falls through to Unmatched, where the planner renders the default destination.
//
// Routing keys on stable ids, never on names. Names appear only in the human-readable matched-rule
// label.
//
// Pure: no System.IO, no Cove types, no DB. The cascade classifies and never throws - a null StudioId,
// an empty ParentStudios or empty destination maps all fall through to Unmatched. Source-path regexes
// arrive pre-parsed in RouteLookups; this resolver only calls IsMatch and never compiles a pattern.
public static class DestinationResolver
{
    // The case rule for exact source-path matching: paths are case-insensitive on Windows, the primary
    // platform, and case-sensitive elsewhere. The exact-path dictionary is built with this comparer so a
    // rule for "media/incoming" matches a stored "Media/Incoming" on Windows instead of silently falling
    // through. Mirrors VolumeClassifier and PathConfinement.IsUnderRoot.
    public static StringComparer SourcePathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Normalizes a source path for exact-match keying: trims a single trailing forward slash so a rule
    // for "media/incoming" also matches a stored "media/incoming/". Separator style is already
    // forward-slash on both sides, and case is the comparer's business. Applied identically when the
    // exact map is built and when the resolver looks a source path up.
    public static string NormalizeSourcePath(string path) => path.TrimEnd('/');

    // Resolves one entity by the locked precedence.
    public static RouteResult Resolve(RenamerEntity e, RenamerOptions o, RouteLookups lk)
    {
        // Excludes run first, beating every routing category including unorganized.
        if (ResolveExclusion(e, lk) is { } excluded)
        {
            return excluded;
        }

        // Unorganized has its own route, ahead of the tag/studio/path cascade.
        if (!e.Organized && o.UnorganizedDestination is { } unorganized)
        {
            return new RouteResult(RouteCategory.Unorganized, "Unorganized", unorganized);
        }

        // Tag: first tag in entity list order whose stable id has a rule. The reason carries the name
        // because a reason is read by a person; the match itself never uses it.
        foreach (var (tagId, tagName) in e.TagRefs)
        {
            if (lk.TagIdToDest.TryGetValue(tagId, out var tagDest))
            {
                return new RouteResult(RouteCategory.Tag, $"Tag:{tagName}", tagDest);
            }
        }

        // Studio including parents, keyed on the stable id; a direct match outranks an ancestor.
        if (e.StudioId is int direct && lk.StudioIdToDest.TryGetValue(direct, out var directDest))
        {
            return new RouteResult(RouteCategory.Studio, $"Studio:{direct}(direct)", directDest);
        }

        if (e.ParentStudios is { } ancestors)
        {
            // ParentStudios is nearest-first; the first ancestor with a rule wins.
            foreach (var (ancestorId, _) in ancestors)
            {
                if (lk.StudioIdToDest.TryGetValue(ancestorId, out var ancestorDest))
                {
                    return new RouteResult(RouteCategory.Studio, $"Studio:{ancestorId}(ancestor)", ancestorDest);
                }
            }
        }

        // Source-path: exact first, then the first matching pre-parsed regex. The entity's source path
        // is its first file's parent folder, so a multi-file item routes by its first file's location.
        if (e.Files.Count > 0)
        {
            var sourcePath = e.Files[0].ParentFolderPath;

            // Normalized the same way the exact map keys were, so a stored "media/incoming/" matches a
            // rule for "media/incoming".
            if (lk.PathExactToDest.TryGetValue(NormalizeSourcePath(sourcePath), out var exactDest))
            {
                return new RouteResult(RouteCategory.SourcePath, "SourcePath:exact", exactDest);
            }

            foreach (var (pattern, regexDest) in lk.PathRegexRules)
            {
                if (TryMatch(pattern, sourcePath) is not { } matched)
                {
                    return TimedOut("SourcePath:regex", pattern);
                }

                if (matched)
                {
                    return new RouteResult(RouteCategory.SourcePath, "SourcePath:regex", regexDest);
                }
            }
        }

        // No rule matched. The item's destination is the default, which the planner reads from the
        // options; this resolver carries none for it, because a rule that did not match has none to
        // carry.
        return new RouteResult(RouteCategory.Unmatched, "Default", null);
    }

    // The exclude cascade: tag id, studio id (direct or any ParentStudios ancestor), then source-path,
    // exact before regex. Returns the excluded result on the first match, or null when nothing excludes
    // the entity. A null or empty exclude lookup means none is configured.
    private static RouteResult? ResolveExclusion(RenamerEntity e, RouteLookups lk)
    {
        if (lk.ExcludeTagIds is { Count: > 0 } excludeTags)
        {
            foreach (var (tagId, tagName) in e.TagRefs)
            {
                if (excludeTags.Contains(tagId))
                {
                    return new RouteResult(RouteCategory.Excluded, $"Exclude:Tag:{tagName}", null);
                }
            }
        }

        // Studio exclude, keyed on the stable id; direct outranks ancestor.
        if (lk.ExcludeStudioIds is { Count: > 0 } excludeStudios)
        {
            if (e.StudioId is int directStudio && excludeStudios.Contains(directStudio))
            {
                return new RouteResult(RouteCategory.Excluded, $"Exclude:Studio:{directStudio}(direct)", null);
            }

            if (e.ParentStudios is { } excludeAncestors)
            {
                // ParentStudios is nearest-first; the first excluded ancestor wins.
                foreach (var (ancestorId, _) in excludeAncestors)
                {
                    if (excludeStudios.Contains(ancestorId))
                    {
                        return new RouteResult(RouteCategory.Excluded, $"Exclude:Studio:{ancestorId}(ancestor)", null);
                    }
                }
            }
        }

        // Source-path exclude: exact first, then the first matching pre-parsed exclude regex.
        if (e.Files.Count > 0
            && (lk.ExcludePathsExact is { Count: > 0 } || lk.ExcludePathRegex is { Count: > 0 }))
        {
            var excludeSrc = e.Files[0].ParentFolderPath;

            if (lk.ExcludePathsExact is { Count: > 0 } excludeExact
                && excludeExact.Contains(NormalizeSourcePath(excludeSrc)))
            {
                return new RouteResult(RouteCategory.Excluded, "Exclude:Path:exact", null);
            }

            if (lk.ExcludePathRegex is { Count: > 0 } excludeRegex)
            {
                foreach (var pattern in excludeRegex)
                {
                    if (TryMatch(pattern, excludeSrc) is not { } matched)
                    {
                        return TimedOut("Exclude:Path:regex", pattern);
                    }

                    if (matched)
                    {
                        return new RouteResult(RouteCategory.Excluded, "Exclude:Path:regex", null);
                    }
                }
            }
        }

        return null;
    }

    // A pattern that compiles can still backtrack past its match timeout. The outcome of that rule is
    // then unknown, and either guess can move a file the user meant to keep in place, so the item is
    // left alone with the rule named. Null means the match timed out.
    private static bool? TryMatch(Regex pattern, string input)
    {
        try
        {
            return pattern.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static RouteResult TimedOut(string rule, Regex pattern)
        => new(RouteCategory.RuleTimedOut, $"{rule}:{pattern}", null);
}

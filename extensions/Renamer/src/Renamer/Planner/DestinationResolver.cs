using Renamer.Options;

namespace Renamer.Planner;

/// <summary>
/// The pure routing brain: maps one <see cref="RenamerEntity"/> to a <see cref="RouteResult"/>
/// (a routed destination-root template, or source-confine) by the deterministic precedence.
/// Called ONCE per entity in the planner, mirroring how <c>MetadataProjector.Project</c>
/// is called once per file.
///
/// PURE: no <c>System.IO</c>, no <c>Cove.*</c> types, no DB. The cascade is classify-not-throw — a
/// null <see cref="RenamerEntity.StudioId"/>, an empty <see cref="RenamerEntity.ParentStudios"/>, or
/// empty destination maps all fall straight through to <see cref="RouteCategory.SourceConfine"/>.
/// The source-path regex set arrives PRE-PARSED in <see cref="RouteLookups.PathRegexRules"/> (built
/// once per batch); this resolver only calls <c>IsMatch</c> — it never compiles a regex.
///
/// Precedence (first CATEGORY that produces a match wins):
/// <c>Excludes → Unorganized → Tag → Studio (incl. parent) → Source-path → Default</c>; within a
/// category the first user-ordered rule wins, and within Studio a DIRECT match outranks an ANCESTOR.
///
/// Excludes run FIRST and beat every routing category including Unorganized: a matching tag name,
/// studio id (direct or any ParentStudios ancestor id), or source-path (exact then regex)
/// short-circuits to <see cref="RouteCategory.Excluded"/> (the planner then produces a
/// <c>SkipExcluded</c> for every file). The exclude lookups arrive PRE-PARSED in the
/// <see cref="RouteLookups"/> (a null/empty member = no excludes = legacy behavior, no regression);
/// an exclude regex match-time timeout is treated as no-match, never thrown. Default-relocate is
/// implemented but GATED — the <see cref="RouteCategory.Default"/> branch is reachable ONLY when
/// <see cref="RenamerOptions.EnableDefaultRelocate"/> is true; the off branch returns
/// <see cref="RouteCategory.SourceConfine"/> as a code-level guard, not merely a config default.
/// </summary>
public static class DestinationResolver
{
    /// <summary>
    /// The OS-aware string comparer for EXACT source-path matching — <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// on Windows (where paths are case-insensitive, the primary platform) and
    /// <see cref="StringComparer.Ordinal"/> elsewhere, mirroring <c>VolumeClassifier</c> /
    /// <c>PathConfinement.IsUnderRoot</c>. The exact-path lookup dictionary is built with this comparer
    /// so an exact rule for <c>media/incoming</c> matches a stored <c>Media/Incoming</c> on Windows
    /// instead of silently falling through.
    /// </summary>
    public static StringComparer SourcePathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Normalizes a source path for EXACT-match keying/lookup — trims a single trailing
    /// forward slash so a rule for <c>media/incoming</c> also matches a stored <c>media/incoming/</c>.
    /// (Separator style is already forward-slash on both the stored <c>ParentFolderPath</c> and the
    /// rule pattern; case is handled by <see cref="SourcePathComparer"/>.) Applied identically when
    /// the exact map is built and when the resolver looks a source path up.
    /// </summary>
    public static string NormalizeSourcePath(string path) => path.TrimEnd('/');

    /// <summary>
    /// The matched-rule label this resolver emits for the GATED default-relocate category
    /// (<see cref="RouteCategory.Default"/>). Exposed as the single source of truth so the auto-renamer
    /// hook can detect a default-relocate route off <see cref="RenamerPlanItem.MatchedRule"/> without
    /// duplicating the literal string.
    /// </summary>
    public const string DefaultRouteLabel = "Default";

    /// <summary>
    /// Resolves <paramref name="e"/> to a <see cref="RouteResult"/> by the locked precedence.
    /// </summary>
    /// <param name="e">The entity to route (read-only; only routing-relevant fields are read).</param>
    /// <param name="o">The renamer options carrying the destination maps + the default-relocate gate.</param>
    /// <param name="lk">The per-batch hoisted lookups (studio-id, tag-name, path-exact, pre-parsed regex).</param>
    public static RouteResult Resolve(RenamerEntity e, RenamerOptions o, RouteLookups lk)
    {
        // 1. Excludes — run FIRST, beating every routing category INCLUDING Unorganized. A matching
        //    tag NAME, studio id (direct OR any ParentStudios ancestor id), or source-path (exact then
        //    regex) returns (Excluded, "Exclude:…", null). The exclude lookups arrive PRE-PARSED in the
        //    RouteLookups (a null member = none configured = legacy behavior, no regression). The
        //    match-time RegexMatchTimeoutException on an exclude regex is treated as no-match (classify,
        //    don't throw), exactly like the source-path routing regex below.

        // 1a. Tag exclude (case-insensitive on the tag NAME, mirroring tag routing).
        if (lk.ExcludeTagNames is { Count: > 0 } excludeTags)
        {
            foreach (var tag in e.Tags)
            {
                if (excludeTags.Contains(tag))
                {
                    return new RouteResult(RouteCategory.Excluded, $"Exclude:Tag:{tag}", null);
                }
            }
        }

        // 1b. Studio exclude (direct outranks ancestor; keyed on the stable id, NEVER the name).
        if (lk.ExcludeStudioIds is { Count: > 0 } excludeStudios)
        {
            if (e.StudioId is int directStudio && excludeStudios.Contains(directStudio))
            {
                return new RouteResult(RouteCategory.Excluded, $"Exclude:Studio:{directStudio}(direct)", null);
            }

            if (e.ParentStudios is { } excludeAncestors)
            {
                // ParentStudios is NEAREST-FIRST; the first excluded ancestor wins.
                foreach (var (ancestorId, _) in excludeAncestors)
                {
                    if (excludeStudios.Contains(ancestorId))
                    {
                        return new RouteResult(RouteCategory.Excluded, $"Exclude:Studio:{ancestorId}(ancestor)", null);
                    }
                }
            }
        }

        // 1c. Source-path exclude: exact FIRST, then the first matching pre-parsed exclude regex.
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
                    // Classify, don't throw: a match-time catastrophic-backtracking timeout is treated
                    // as "this rule did not match" — skip it, never an uncaught throw that aborts the
                    // batch. The build-time guard already rejected syntax-invalid patterns.
                    bool excluded;
                    try
                    {
                        excluded = pattern.IsMatch(excludeSrc);
                    }
                    catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                    {
                        continue;
                    }

                    if (excluded)
                    {
                        return new RouteResult(RouteCategory.Excluded, "Exclude:Path:regex", null);
                    }
                }
            }
        }

        // 2. Unorganized: its own route, BEFORE the tag/studio/path cascade.
        if (!e.Organized && !string.IsNullOrEmpty(o.UnorganizedDestination))
        {
            return new RouteResult(RouteCategory.Unorganized, "Unorganized", o.UnorganizedDestination);
        }

        // 3. Cascade — first CATEGORY that produces a match wins.
        // 3a. Tag: first tag in entity list order whose name (OrdinalIgnoreCase) has a rule.
        foreach (var tag in e.Tags)
        {
            if (lk.TagNameToDest.TryGetValue(tag, out var tagDest))
            {
                return new RouteResult(RouteCategory.Tag, $"Tag:{tag}", tagDest);
            }
        }

        // 3b. Studio incl. parent — DIRECT outranks ANCESTOR; keyed on the stable id.
        if (e.StudioId is int direct && lk.StudioIdToDest.TryGetValue(direct, out var directDest))
        {
            return new RouteResult(RouteCategory.Studio, $"Studio:{direct}(direct)", directDest);
        }

        if (e.ParentStudios is { } ancestors)
        {
            // ParentStudios is NEAREST-FIRST; the first ancestor with a rule wins.
            foreach (var (ancestorId, _) in ancestors)
            {
                if (lk.StudioIdToDest.TryGetValue(ancestorId, out var ancestorDest))
                {
                    return new RouteResult(RouteCategory.Studio, $"Studio:{ancestorId}(ancestor)", ancestorDest);
                }
            }
        }

        // 3c. Source-path: exact FIRST, then the first matching pre-parsed regex. The entity's source
        //     path is its first file's parent folder (per-entity routing; a multi-file item routes by
        //     its first file's location).
        if (e.Files.Count > 0)
        {
            var sourcePath = e.Files[0].ParentFolderPath;

            // Normalize the source path the SAME way the exact map keys were normalized (OS-aware case
            // via SourcePathComparer baked into the dict + trailing-slash trim here) so a stored
            // "media/incoming/" matches a rule for "media/incoming" on Windows.
            if (lk.PathExactToDest.TryGetValue(NormalizeSourcePath(sourcePath), out var exactDest))
            {
                return new RouteResult(RouteCategory.SourcePath, "SourcePath:exact", exactDest);
            }

            foreach (var (pattern, regexDest) in lk.PathRegexRules)
            {
                // A pattern that COMPILES fine but exhibits catastrophic backtracking (e.g. ^(a+)+$
                // against a long non-matching path) throws RegexMatchTimeoutException at MATCH time once
                // the per-pattern timeout elapses (the build-time guard only catches syntax errors).
                // Classify, don't throw: a match-time timeout is treated as "this rule did not match" —
                // skip it and keep cascading — NEVER an uncaught throw that aborts the whole batch. The
                // timeout already bounds the hang; this bounds the blast radius to one rule. (The
                // resolver is pure/static, so it cannot log here; the bound + skip is the contract.)
                bool matched;
                try
                {
                    matched = pattern.IsMatch(sourcePath);
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    continue;
                }

                if (matched)
                {
                    return new RouteResult(RouteCategory.SourcePath, "SourcePath:regex", regexDest);
                }
            }
        }

        // 4. Default — GATED: reachable ONLY when the flag is on AND a default is set. A code-level
        //    guard, NOT just a config default — an unmatched item NEVER silently relocates while
        //    EnableDefaultRelocate is false (it stays gated until volume-aware undo exists).
        if (o.EnableDefaultRelocate && !string.IsNullOrEmpty(o.DefaultDestination))
        {
            return new RouteResult(RouteCategory.Default, DefaultRouteLabel, o.DefaultDestination);
        }

        // 5. No route → source-confine. The default-relocate-disabled false branch lands HERE, so an
        //    unmatched item keeps its own parent-folder anchor and does not relocate.
        return new RouteResult(RouteCategory.SourceConfine, "InPlace", null);
    }
}

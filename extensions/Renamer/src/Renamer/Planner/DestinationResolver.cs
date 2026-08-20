using Renamer.Options;

namespace Renamer.Planner;

/// <summary>
/// The pure routing brain: maps one <see cref="RenamerEntity"/> to a <see cref="RouteResult"/>
/// (a routed destination-root template, or source-confine) by the deterministic precedence.
/// Called ONCE per entity in the planner, mirroring how <c>MetadataProjector.Project</c>
/// is called once per file.
///
/// PURE: no <c>System.IO</c>, no <c>Cove.*</c> types, no DB. The cascade is classify-not-throw — a
/// null <see cref="RenamerEntity.StudioId"/>, an empty <see cref="RenamerEntity.ParentStudios"/>, an
/// empty <see cref="RenamerEntity.TagRefs"/>, or empty destination maps all fall straight through to
/// <see cref="RouteCategory.SourceConfine"/>.
/// The source-path regex set arrives PRE-PARSED in <see cref="RouteLookups.PathRegexRules"/> (built
/// once per batch); this resolver only calls <c>IsMatch</c> — it never compiles a regex.
///
/// Precedence (first CATEGORY that produces a match wins):
/// <c>Excludes → Unorganized → Tag → Studio (incl. parent) → Source-path</c>; within a
/// category the first user-ordered rule wins, and within Studio a DIRECT match outranks an ANCESTOR.
///
/// Excludes run FIRST and beat every routing category including Unorganized: a matching tag id,
/// studio id (direct or any ParentStudios ancestor id), or source-path (exact then regex)
/// short-circuits to <see cref="RouteCategory.Excluded"/> (the planner then produces a
/// <c>SkipExcluded</c> for every file). The exclude lookups arrive PRE-PARSED in the
/// <see cref="RouteLookups"/> (a null/empty member = no excludes = legacy behavior, no regression);
/// an exclude regex match-time timeout is treated as no-match, never thrown. An item that matches no
/// rule is never relocated: it falls through to <see cref="RouteCategory.SourceConfine"/> and keeps
/// its own parent-folder anchor.
/// </summary>
public static class DestinationResolver
{
    /// <summary>
    /// The OS-aware string comparer for EXACT source-path matching — <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// where the default filesystem folds case (Windows, macOS) and <see cref="StringComparer.Ordinal"/>
    /// elsewhere, matching <c>PathOps.PathsEqual</c> and <c>PathConfinement.IsUnderRoot</c> (the case
    /// rule and its caveats are stated once at <c>PathOps.PathsEqual</c>; note <c>VolumeClassifier</c> is
    /// NOT part of that set — it compares volume keys, not filenames). The exact-path lookup dictionary
    /// is built with this comparer so an exact rule for <c>media/incoming</c> matches a stored
    /// <c>Media/Incoming</c> on such a filesystem instead of silently falling through.
    /// </summary>
    public static StringComparer SourcePathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// Normalizes a source path for EXACT-match keying/lookup — trims a single trailing
    /// forward slash so a rule for <c>media/incoming</c> also matches a stored <c>media/incoming/</c>.
    /// (Separator style is already forward-slash on both the stored <c>ParentFolderPath</c> and the
    /// rule pattern; case is handled by <see cref="SourcePathComparer"/>.) Applied identically when
    /// the exact map is built and when the resolver looks a source path up.
    /// </summary>
    public static string NormalizeSourcePath(string path) => path.TrimEnd('/');

    /// <summary>
    /// Resolves <paramref name="e"/> to a <see cref="RouteResult"/> by the locked precedence.
    /// </summary>
    /// <param name="e">The entity to route (read-only; only routing-relevant fields are read).</param>
    /// <param name="o">The renamer options carrying the destination maps.</param>
    /// <param name="lk">The per-batch hoisted lookups (studio-id, tag-name, path-exact, pre-parsed regex).</param>
    public static RouteResult Resolve(RenamerEntity e, RenamerOptions o, RouteLookups lk)
    {
        // 1. Excludes run FIRST, beating every routing category INCLUDING Unorganized.
        if (ResolveExclusion(e, lk) is { } excluded)
        {
            return excluded;
        }

        // 2. Unorganized: its own route, BEFORE the tag/studio/path cascade.
        if (!e.Organized && !string.IsNullOrEmpty(o.UnorganizedDestination))
        {
            return new RouteResult(RouteCategory.Unorganized, "Unorganized", o.UnorganizedDestination);
        }

        // 3. Cascade — first CATEGORY that produces a match wins.
        // 3a. Tag: first tag in entity list order whose stable id has a rule. The name is carried out
        //     of the pair only to build the label — matching never touches it.
        foreach (var (tagId, tagName) in e.TagRefs)
        {
            if (lk.TagIdToDest.TryGetValue(tagId, out var tagDest))
            {
                return new RouteResult(RouteCategory.Tag, $"Tag:{tagName}", tagDest);
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

        // 4. No route → source-confine: an unmatched item keeps its own parent-folder anchor and does
        //    not relocate. There is no catch-all destination — relocating requires an explicit rule.
        return new RouteResult(RouteCategory.SourceConfine, "InPlace", null);
    }

    /// <summary>
    /// The exclude cascade — tag id, studio id (direct OR any ParentStudios ancestor id), then
    /// source-path (exact FIRST, then the first matching pre-parsed regex). Returns the
    /// <see cref="RouteCategory.Excluded"/> result on the first match, or <c>null</c> when nothing
    /// excludes the entity.
    /// </summary>
    /// <remarks>
    /// The exclude lookups arrive PRE-PARSED in the <see cref="RouteLookups"/> (a null/empty member =
    /// none configured = legacy behavior, no regression). A match-time <c>RegexMatchTimeoutException</c>
    /// on an exclude regex is treated as no-match (classify, don't throw), like the routing regex.
    /// </remarks>
    private static RouteResult? ResolveExclusion(RenamerEntity e, RouteLookups lk)
    {
        // Tag exclude (on the stable tag id, mirroring tag routing; the name only builds the label).
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

        // Studio exclude (direct outranks ancestor; keyed on the stable id, NEVER the name).
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

        // Source-path exclude: exact FIRST, then the first matching pre-parsed exclude regex.
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
                    bool matched;
                    try
                    {
                        matched = pattern.IsMatch(excludeSrc);
                    }
                    catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                    {
                        continue;
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
}
